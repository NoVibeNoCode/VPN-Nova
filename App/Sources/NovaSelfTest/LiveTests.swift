import Foundation
import NovaCore

/// Routing + kill switch against an unreachable server, through a local mixed proxy (no root).
func liveRoutingTests() async {
    T.section("Живая маршрутизация: мёртвый сервер, kill switch, «всё остальное»")
    let proxyPort = 17891
    var live = Profile()
    live.settings.clashPort = 19291
    live.settings.defaultTarget = Target.direct
    live.servers = [Server(id: "dead", name: "Мёртвый", code: "XX", colorHex: "#FF5C6C",
                           outbound: ["type": "shadowsocks", "server": "127.0.0.1", "server_port": 9,
                                      "method": "aes-256-gcm", "password": "x"])]
    live.domainRules = [
        DomainRule(kind: .domain, value: "example.net", target: Target.direct),
        DomainRule(kind: .domain, value: "example.com", target: "dead"),
        DomainRule(kind: .keyword, value: "iana", target: "dead"),
    ]
    var off = DomainRule(kind: .domain, value: "example.org", target: "dead")
    off.enabled = false
    live.domainRules.append(off)
    live.appRules = [AppRule(name: "curl", path: "/usr/bin", target: "dead")]

    let core = LocalCore(binary: T.singBox, workDir: T.work.appendingPathComponent("live"))
    do {
        try core.apply(config: ConfigBuilder(profile: live, inbound: .mixed(port: proxyPort)).data())
        try core.start()
    } catch {
        T.check(false, "ядро стартует", "\(error)")
        return
    }
    defer { try? core.stop() }

    let api = ClashAPI(port: live.settings.clashPort, secret: live.clashSecret)
    T.check(await T.waitFor { await api.isAlive() }, "Clash API отвечает")
    let intruder = ClashAPI(port: live.settings.clashPort, secret: "wrong")
    T.check(await intruder.selected("via-dead") == nil, "Clash API отвергает чужой секрет")
    do {
        try await intruder.select("via-dead", "direct")
        T.check(false, "чужой секрет не может переключить селектор")
    } catch {
        T.check(true, "чужой секрет не может переключить селектор")
    }

    T.check(T.curl("https://example.net", proxyPort: proxyPort), "домен → direct работает")
    T.check(!T.curl("https://example.com", proxyPort: proxyPort), "домен → мёртвый сервер: заблокировано (kill switch)")
    T.check(!T.curl("https://www.iana.org", proxyPort: proxyPort), "ключевое слово → мёртвый сервер: заблокировано")
    T.check(T.curl("https://example.org", proxyPort: proxyPort), "выключенное правило не действует")
    if getuid() == 0 {
        T.check(!T.curl("https://www.wikipedia.org", proxyPort: proxyPort), "правило приложения: curl → мёртвый сервер")
    } else {
        T.skip("правило приложения через локальный прокси", "macOS 27 не показывает таблицу соединений без root; проверяется в TUN-тесте")
    }

    let monitor = HealthMonitor(api: api)
    await monitor.tick(serverIDs: ["dead"], killSwitch: false)
    T.check(monitor.health["dead"]?.isDown == false && monitor.health["dead"]?.failures == 1, "один провал ещё не «упал» (защита от ложных срабатываний)")
    T.check(await api.selected("via-dead") == "srv-dead", "после одного провала селектор не трогаем")
    await monitor.tick(serverIDs: ["dead"], killSwitch: false)
    T.check(monitor.health["dead"]?.isDown == true, "два провала подряд → сервер упал")
    T.check(await api.selected("via-dead") == "direct", "kill switch выкл → селектор на direct")
    T.check(T.curl("https://example.com", proxyPort: proxyPort), "kill switch выкл → трафик идёт напрямую")
    await monitor.tick(serverIDs: ["dead"], killSwitch: true)
    T.check(await api.selected("via-dead") == "srv-dead", "kill switch вкл → селектор вернулся на сервер")
    T.check(!T.curl("https://example.com", proxyPort: proxyPort), "kill switch вкл → снова заблокировано")
    await monitor.tick(serverIDs: [], killSwitch: true)
    T.check(monitor.health.isEmpty, "удалённые серверы пропадают из мониторинга")

    T.check(T.curl("https://example.org", proxyPort: proxyPort), "всё остальное → direct")
    try? await api.select(ConfigBuilder.defaultRouteTag, ConfigBuilder.selectorTag("dead"))
    T.check(!T.curl("https://example.org", proxyPort: proxyPort), "всё остальное переключено на лету → заблокировано")
    try? await api.select(ConfigBuilder.defaultRouteTag, ConfigBuilder.directTag)
    T.check(T.curl("https://example.org", proxyPort: proxyPort), "и обратно на direct без перезапуска")

    let conns = try? await api.connections()
    T.check(conns != nil, "список соединений читается")
    var traffic = false
    do { for try await _ in api.traffic() { traffic = true; break } } catch {}
    T.check(traffic, "поток трафика читается")
    await api.closeAllConnections()
    let after = try? await api.connections()
    T.check((after?.connections ?? []).isEmpty, "закрытие всех соединений работает")

    try? core.stop()
    T.check(!(await api.isAlive()), "после остановки ядра API недоступен (приложение это заметит)")
}

/// End-to-end with real protocol servers on localhost: share link → parser → config →
/// traffic actually arrives at the right server. Then kill switch recovery.
func protocolServerTests() async {
    T.section("Сквозной тест: настоящие серверы 6 протоколов на localhost")
    let uuid = "6f1f0f3e-9a0e-4b7c-9c1e-2b8f1c7e5a11"
    let ssPass = "c3VwZXItc2VjcmV0LXBhc3N3b3Jk"

    // Reality keys and a self-signed cert for the TLS-based servers.
    let (_, kp) = T.shell(T.singBox, ["generate", "reality-keypair"])
    let priv = kp.components(separatedBy: "\n").first { $0.hasPrefix("PrivateKey:") }?.split(separator: " ").last.map(String.init) ?? ""
    let pub = kp.components(separatedBy: "\n").first { $0.hasPrefix("PublicKey:") }?.split(separator: " ").last.map(String.init) ?? ""
    let (_, pem) = T.shell(T.singBox, ["generate", "tls-keypair", "nova.test"])
    let keyEnd = pem.range(of: "-----END PRIVATE KEY-----")
    guard !priv.isEmpty, !pub.isEmpty, let keyEnd else {
        T.check(false, "ключи для тестовых серверов", kp + pem)
        return
    }
    let keyPEM = String(pem[..<keyEnd.upperBound]) + "\n"
    let certPEM = String(pem[keyEnd.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines) + "\n"
    let certDir = T.work.appendingPathComponent("certs")
    try? FileManager.default.createDirectory(at: certDir, withIntermediateDirectories: true)
    let keyPath = certDir.appendingPathComponent("key.pem").path
    let certPath = certDir.appendingPathComponent("cert.pem").path
    try? keyPEM.write(toFile: keyPath, atomically: true, encoding: .utf8)
    try? certPEM.write(toFile: certPath, atomically: true, encoding: .utf8)
    let tls: JSONValue = ["enabled": true, "server_name": "nova.test", "certificate_path": .string(certPath), "key_path": .string(keyPath)]

    let serverConfig: JSONValue = [
        "log": ["level": "info"],
        "inbounds": [
            ["type": "shadowsocks", "tag": "ss-in", "listen": "127.0.0.1", "listen_port": 18388, "method": "aes-256-gcm", "password": .string(ssPass)],
            ["type": "vmess", "tag": "vmess-in", "listen": "127.0.0.1", "listen_port": 18389, "users": [["uuid": .string(uuid), "alterId": 0]]],
            ["type": "vless", "tag": "vless-in", "listen": "127.0.0.1", "listen_port": 18443,
             "users": [["uuid": .string(uuid), "flow": "xtls-rprx-vision"]],
             "tls": ["enabled": true, "server_name": "www.apple.com",
                     "reality": ["enabled": true, "handshake": ["server": "www.apple.com", "server_port": 443],
                                 "private_key": .string(priv), "short_id": ["0123abcd"]]]],
            ["type": "trojan", "tag": "trojan-in", "listen": "127.0.0.1", "listen_port": 18444, "users": [["password": "tr-pass"]], "tls": tls],
            ["type": "hysteria2", "tag": "hy2-in", "listen": "127.0.0.1", "listen_port": 18445, "users": [["password": "hy-pass"]], "tls": tls],
            ["type": "tuic", "tag": "tuic-in", "listen": "127.0.0.1", "listen_port": 18446,
             "users": [["uuid": .string(uuid), "password": "tu-pass"]], "congestion_control": "bbr",
             "tls": ["enabled": true, "server_name": "nova.test", "alpn": ["h3"], "certificate_path": .string(certPath), "key_path": .string(keyPath)]],
        ],
        "outbounds": [["type": "direct", "tag": "direct"]],
    ]

    var server: TestServer
    do {
        server = try TestServer(config: serverConfig, name: "multi")
    } catch {
        T.check(false, "тестовые серверы стартуют", "\(error)")
        return
    }
    defer { server.stop() }
    try? await Task.sleep(nanoseconds: 1_200_000_000)
    T.check(server.process.isRunning, "тестовые серверы стартуют", server.logText)

    func vmessLink() -> String {
        "vmess://" + Data(#"{"v":"2","ps":"VM","add":"127.0.0.1","port":"18389","id":"\#(uuid)","aid":"0","net":"tcp"}"#.utf8).base64EncodedString()
    }
    let cases: [(id: String, link: String, domain: String, inbound: String)] = [
        ("vl", "vless://\(uuid)@127.0.0.1:18443?type=tcp&security=reality&pbk=\(pub)&sid=0123abcd&sni=www.apple.com&fp=chrome&flow=xtls-rprx-vision#VLESS", "example.com", "vless-in"),
        ("ss", "ss://" + Data("aes-256-gcm:\(ssPass)".utf8).base64EncodedString() + "@127.0.0.1:18388#SS", "example.net", "ss-in"),
        ("vm", vmessLink(), "example.org", "vmess-in"),
        ("tr", "trojan://tr-pass@127.0.0.1:18444?sni=nova.test&allowInsecure=1#TR", "www.cloudflare.com", "trojan-in"),
        ("hy", "hy2://hy-pass@127.0.0.1:18445?sni=nova.test&insecure=1#HY", "www.wikipedia.org", "hy2-in"),
        ("tu", "tuic://\(uuid):tu-pass@127.0.0.1:18446?sni=nova.test&insecure=1&alpn=h3&congestion_control=bbr#TU", "www.gstatic.com", "tuic-in"),
    ]

    var p = Profile()
    p.settings.clashPort = 19292
    p.settings.defaultTarget = Target.direct
    for c in cases {
        guard let parsed = try? LinkParser.parse(c.link) else {
            T.check(false, "ссылка \(c.id) разбирается", c.link)
            continue
        }
        p.servers.append(Server(id: c.id, name: parsed.name, code: c.id.uppercased(), colorHex: "#8B7CFF", outbound: parsed.outbound))
        p.domainRules.append(DomainRule(kind: .domain, value: c.domain, target: c.id))
    }

    let proxyPort = 17892
    let core = LocalCore(binary: T.singBox, workDir: T.work.appendingPathComponent("e2e"))
    do {
        try core.apply(config: ConfigBuilder(profile: p, inbound: .mixed(port: proxyPort)).data())
        try core.start()
    } catch {
        T.check(false, "клиент стартует", "\(error)")
        return
    }
    defer { try? core.stop() }
    let api = ClashAPI(port: p.settings.clashPort, secret: p.clashSecret)
    T.check(await T.waitFor { await api.isAlive() }, "клиент поднялся")

    // Warm-up: the first request through each server pays for its cold DNS / Reality handshake.
    let monitor = HealthMonitor(api: api)
    _ = await monitor.tick(serverIDs: cases.map(\.id), killSwitch: true)

    func arrived(_ log: String, inbound: String, domain: String) -> Bool {
        log.components(separatedBy: "\n").contains { $0.contains("[\(inbound)]: ") && $0.contains("inbound connection to \(domain):443") }
    }

    for c in cases {
        var ok = T.curl("https://\(c.domain)", proxyPort: proxyPort, timeout: 15)
        if !ok { ok = T.curl("https://\(c.domain)", proxyPort: proxyPort, timeout: 15) }
        let arrived = arrived(server.logText, inbound: c.inbound, domain: c.domain)
        T.check(ok && arrived, "\(c.inbound.replacingOccurrences(of: "-in", with: "")): \(c.domain) ушёл именно на свой сервер",
                "curl=\(ok) в логе сервера=\(arrived)")
    }

    var h = await monitor.tick(serverIDs: cases.map(\.id), killSwitch: true)
    if cases.contains(where: { h[$0.id]?.delayMs == nil }) {
        h = await monitor.tick(serverIDs: cases.map(\.id), killSwitch: true)  // one retry, like the real monitor
    }
    for c in cases {
        T.check(h[c.id]?.delayMs != nil, "задержка через \(c.id) измеряется (\(h[c.id]?.delayMs.map { "\($0) мс" } ?? "нет"))")
    }

    T.section("Kill switch: сервер падает и поднимается")
    server.stop()
    try? await Task.sleep(nanoseconds: 500_000_000)
    for _ in 0..<HealthMonitor.failuresToMarkDown { await monitor.tick(serverIDs: ["vl"], killSwitch: true) }
    T.check(monitor.health["vl"]?.isDown == true, "падение сервера замечено")
    T.check(!T.curl("https://example.com", proxyPort: proxyPort, timeout: 6), "kill switch вкл: трафик упавшего сервера заблокирован")
    await monitor.tick(serverIDs: ["vl"], killSwitch: false)
    T.check(T.curl("https://example.com", proxyPort: proxyPort), "выключили kill switch → сразу пошло напрямую")

    do {
        server = try TestServer(config: serverConfig, name: "multi-2")
    } catch {
        T.check(false, "сервер перезапускается", "\(error)")
        return
    }
    try? await Task.sleep(nanoseconds: 1_200_000_000)
    // The app re-checks every ~10 s; allow a few rounds for the fresh server's cold DNS.
    var rounds = 0
    repeat {
        rounds += 1
        await monitor.tick(serverIDs: ["vl"], killSwitch: false)
    } while monitor.health["vl"]?.isDown != false && rounds < 6
    T.check(monitor.health["vl"]?.isDown == false, "сервер снова доступен (проверок: \(rounds))")
    T.check(await api.selected("via-vl") == "srv-vl", "селектор сам вернулся на сервер")
    _ = T.curl("https://example.com", proxyPort: proxyPort, timeout: 15)
    T.check(arrived(server.logText, inbound: "vless-in", domain: "example.com"), "трафик снова идёт через сервер")
}
