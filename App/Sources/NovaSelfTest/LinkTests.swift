import Foundation
import NovaCore

/// Share-link parsing: every protocol, transports, odd encodings, and rejections.
/// Every successfully parsed outbound must also pass `sing-box check`.
func linkTests() {
    T.section("Ссылки: разбор")
    var parsed: [(String, JSONValue)] = []

    func ok(_ link: String, _ name: String, _ verify: (LinkParser.Parsed) -> Bool) {
        do {
            let p = try LinkParser.parse(link)
            T.check(verify(p), name, p.outbound.prettyString())
            parsed.append((name, p.outbound))
        } catch {
            T.check(false, name, "ошибка: \(error.localizedDescription)")
        }
    }

    func fails(_ link: String, _ name: String, contains: String? = nil) {
        do {
            let p = try LinkParser.parse(link)
            T.check(false, name, "ожидалась ошибка, получено: \(p.outbound.prettyString())")
        } catch {
            let msg = error.localizedDescription
            T.check(contains.map { msg.contains($0) } ?? true, "\(name) → «\(msg)»")
        }
    }

    let uuid = "11111111-2222-3333-4444-555555555555"
    let pbk = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0"

    // VLESS
    ok("vless://\(uuid)@203.0.113.10:443?type=tcp&security=reality&pbk=\(pbk)&sid=0123abcd&sni=www.microsoft.com&fp=chrome&flow=xtls-rprx-vision#DE%20Server", "vless reality vision") {
        $0.name == "DE Server" && $0.outbound["flow"]?.stringValue == "xtls-rprx-vision"
            && $0.outbound["tls"]?["reality"]?["short_id"]?.stringValue == "0123abcd"
            && $0.outbound["tls"]?["utls"]?["fingerprint"]?.stringValue == "chrome"
            && $0.outbound["tls"]?["server_name"]?.stringValue == "www.microsoft.com"
            && $0.outbound["transport"] == nil
    }
    ok("vless://\(uuid)@1.2.3.4:443?security=reality&pbk=\(pbk)&sni=a.com", "vless reality без fp и sid → chrome, пустой sid") {
        $0.outbound["tls"]?["utls"]?["fingerprint"]?.stringValue == "chrome" && $0.outbound["tls"]?["reality"]?["short_id"]?.stringValue == ""
    }
    ok("VLESS://\(uuid)@1.2.3.4:443?security=reality&pbk=\(pbk)  \n", "схема в верхнем регистре и мусор по краям") { $0.outbound["server"]?.stringValue == "1.2.3.4" }
    ok("vless://\(uuid)@[2001:db8::1]:8443?security=tls&sni=v6.example#v6", "vless IPv6-адрес") {
        $0.outbound["server"]?.stringValue == "2001:db8::1" && $0.outbound["server_port"]?.intValue == 8443
    }
    ok("vless://\(uuid)@h.example:443?type=ws&security=tls&path=%2Fws%3Fed%3D2048&host=cdn.example&sni=cdn.example", "vless ws+tls, путь закодирован") {
        $0.outbound["transport"]?["type"]?.stringValue == "ws" && $0.outbound["transport"]?["path"]?.stringValue == "/ws?ed=2048"
            && $0.outbound["transport"]?["headers"]?["Host"]?.stringValue == "cdn.example"
    }
    ok("vless://\(uuid)@h.example:443?type=grpc&serviceName=gun&security=tls", "vless grpc") {
        $0.outbound["transport"]?["service_name"]?.stringValue == "gun"
    }
    ok("vless://\(uuid)@h.example:80?type=httpupgrade&path=/up&host=h.example", "vless httpupgrade без TLS") {
        $0.outbound["transport"]?["type"]?.stringValue == "httpupgrade" && $0.outbound["tls"] == nil
    }
    ok("vless://\(uuid)@h.example:443?type=h2&security=tls&host=a.example,b.example&path=/h2", "vless h2 → http-транспорт с host-списком") {
        $0.outbound["transport"]?["type"]?.stringValue == "http" && $0.outbound["transport"]?["host"] == .array(["a.example", "b.example"])
    }
    ok("vless://\(uuid)@h.example:443?security=tls&alpn=h2,http/1.1&allowInsecure=1", "vless tls: alpn и insecure") {
        $0.outbound["tls"]?["alpn"] == .array(["h2", "http/1.1"]) && $0.outbound["tls"]?["insecure"] == .bool(true)
    }
    ok("vless://\(uuid)@h.example:443#%F0%9F%87%A9%F0%9F%87%AA%20Frankfurt", "имя с флагом-эмодзи") { $0.name == "🇩🇪 Frankfurt" }
    ok("vless://\(uuid)@h.example:443/?security=none", "слэш перед query") { $0.outbound["server_port"]?.intValue == 443 }
    fails("vless://\(uuid)@1.2.3.4?security=reality&pbk=\(pbk)", "vless без порта")
    fails("vless://@1.2.3.4:443", "vless без UUID")
    fails("vless://\(uuid)@1.2.3.4:443?security=reality", "reality без pbk", contains: "pbk")
    fails("vless://\(uuid)@1.2.3.4:8443?type=xhttp&security=reality&pbk=\(pbk)", "XHTTP", contains: "xhttp")
    fails("vless://\(uuid)@1.2.3.4:8443?type=splithttp", "SplitHTTP", contains: "splithttp")
    fails("vless://\(uuid)@1.2.3.4:abc", "порт не число")

    // Trojan
    ok("trojan://p%40ss@t.example:443?sni=t.example#TR", "trojan: TLS по умолчанию, пароль с @") {
        $0.outbound["password"]?.stringValue == "p@ss" && $0.outbound["tls"]?["enabled"] == .bool(true)
    }
    ok("trojan://pass@t.example:443?type=ws&path=/tr&security=tls&allowInsecure=1", "trojan ws, insecure") {
        $0.outbound["transport"]?["type"]?.stringValue == "ws" && $0.outbound["tls"]?["insecure"] == .bool(true)
    }

    // VMess
    func vmess(_ json: String) -> String { "vmess://" + Data(json.utf8).base64EncodedString() }
    ok(vmess(#"{"v":"2","ps":"VM","add":"9.9.9.9","port":"443","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","aid":"0","net":"ws","path":"/ws","host":"h.example","tls":"tls","sni":"s.example"}"#), "vmess ws+tls, порт строкой") {
        $0.name == "VM" && $0.outbound["server_port"]?.intValue == 443 && $0.outbound["tls"]?["server_name"]?.stringValue == "s.example"
    }
    ok(vmess(#"{"add":"9.9.9.9","port":10086,"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","aid":0,"net":"tcp","scy":"chacha20-poly1305"}"#), "vmess tcp, порт числом, без имени") {
        $0.name == "9.9.9.9" && $0.outbound["security"]?.stringValue == "chacha20-poly1305" && $0.outbound["tls"] == nil
    }
    ok(vmess(#"{"add":"9.9.9.9","port":"443","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","net":"grpc","path":"svc","tls":"tls"}"#), "vmess grpc") {
        $0.outbound["transport"]?["service_name"]?.stringValue == "svc"
    }
    let urlSafe = Data(#"{"add":"9.9.9.9","port":"8080","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","ps":"???>>>"}"#.utf8).base64EncodedString()
        .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    ok("vmess://" + urlSafe, "vmess URL-safe base64 без паддинга") { $0.name == "???>>>" }
    fails("vmess://not-base64!!", "vmess мусор")

    // Shadowsocks
    ok("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.2.3.4:8388#SS", "ss SIP002 base64") {
        $0.outbound["method"]?.stringValue == "aes-256-gcm" && $0.outbound["password"]?.stringValue == "password"
    }
    ok("ss://2022-blake3-aes-128-gcm:MTIzNDU2Nzg5MDEy%2BzQ1Ng%3D%3D@5.6.7.8:443#x", "ss 2022, открытый userinfo") {
        $0.outbound["password"]?.stringValue == "MTIzNDU2Nzg5MDEy+zQ1Ng=="
    }
    let legacy = Data("chacha20-ietf-poly1305:p:ss@w0rd@ss.example:8389".utf8).base64EncodedString()
    ok("ss://\(legacy)#Legacy", "ss старый формат целиком в base64, пароль с : и @") {
        $0.outbound["password"]?.stringValue == "p:ss@w0rd" && $0.outbound["server"]?.stringValue == "ss.example"
            && $0.outbound["server_port"]?.intValue == 8389
    }
    ok("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@[2001:db8::2]:8388", "ss IPv6") { $0.outbound["server"]?.stringValue == "2001:db8::2" }
    ok("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.2.3.4:8388/?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com#obfs", "ss с плагином obfs") {
        $0.outbound["plugin"]?.stringValue == "obfs-local" && $0.outbound["plugin_opts"]?.stringValue == "obfs=http;obfs-host=bing.com"
    }
    fails("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.2.3.4:8388/?plugin=kcptun", "ss с неподдерживаемым плагином", contains: "kcptun")

    // Hysteria2
    ok("hy2://secret@7.7.7.7:8443?sni=a.example&obfs=salamander&obfs-password=xx#HY", "hysteria2 obfs") {
        $0.outbound["obfs"]?["password"]?.stringValue == "xx" && $0.outbound["tls"]?["server_name"]?.stringValue == "a.example"
    }
    ok("hysteria2://secret@7.7.7.7?insecure=1", "hysteria2 без порта → 443") {
        $0.outbound["server_port"]?.intValue == 443 && $0.outbound["tls"]?["insecure"] == .bool(true)
    }
    ok("hy2://secret@h.example:443,20000-30000/?sni=h.example", "hysteria2 port hopping") {
        $0.outbound["server_port"]?.intValue == 443 && $0.outbound["server_ports"] == .array(["20000:30000"])
    }
    ok("hy2://secret@[2001:db8::3]:9443", "hysteria2 IPv6") { $0.outbound["server"]?.stringValue == "2001:db8::3" && $0.outbound["server_port"]?.intValue == 9443 }

    // TUIC
    ok("tuic://\(uuid):pw@8.8.8.8:443?congestion_control=bbr&alpn=h3&sni=t.example#TU", "tuic") {
        $0.outbound["congestion_control"]?.stringValue == "bbr" && $0.outbound["password"]?.stringValue == "pw"
    }
    fails("tuic://\(uuid)@8.8.8.8:443", "tuic без пароля")

    // Garbage
    fails("wireguard://abc", "неизвестная схема", contains: "не поддерживается")
    fails("hello world", "просто текст")
    fails("", "пустая строка")

    T.section("Ссылки: каждый разобранный outbound проходит sing-box check")
    for (i, (name, ob)) in parsed.enumerated() {
        var p = Profile()
        p.servers = [Server(id: "s\(i)", name: name, code: "S", colorHex: "#fff", outbound: ob)]
        p.settings.defaultTarget = "s\(i)"
        let (good, out) = T.singBoxCheck(try! ConfigBuilder(profile: p).data(), name: "link\(i)")
        T.check(good, name, out)
    }
}
