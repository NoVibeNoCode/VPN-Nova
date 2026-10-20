using System.Text;
using System.Text.Json.Nodes;
using Nova.Core;

namespace Nova.SelfTest;

/// Share-link parsing: every protocol, transports, odd encodings, and rejections.
/// Every successfully parsed outbound must also pass `sing-box check`.
static class LinkTests
{
    public static void Run()
    {
        T.Section("Ссылки: разбор");
        var parsed = new List<(string Name, JsonObject Outbound)>();

        void Ok(string link, string name, Func<LinkParser.Parsed, bool> verify)
        {
            try
            {
                var p = LinkParser.Parse(link);
                T.Check(verify(p), name, p.Outbound.Pretty());
                parsed.Add((name, p.Outbound));
            }
            catch (LinkParser.ParseException e)
            {
                T.Check(false, name, $"ошибка: {e.Message}");
            }
        }

        void Fails(string link, string name, string? contains = null)
        {
            try
            {
                var p = LinkParser.Parse(link);
                T.Check(false, name, $"ожидалась ошибка, получено: {p.Outbound.Pretty()}");
            }
            catch (LinkParser.ParseException e)
            {
                T.Check(contains == null || e.Message.Contains(contains), $"{name} → «{e.Message}»");
            }
        }

        static string? S(JsonNode? n) => n.StringValue();

        const string uuid = "11111111-2222-3333-4444-555555555555";
        const string pbk = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

        // VLESS
        Ok($"vless://{uuid}@203.0.113.10:443?type=tcp&security=reality&pbk={pbk}&sid=0123abcd&sni=www.microsoft.com&fp=chrome&flow=xtls-rprx-vision#DE%20Server", "vless reality vision",
           p => p.Name == "DE Server" && S(p.Outbound["flow"]) == "xtls-rprx-vision"
                && S(p.Outbound["tls"]?["reality"]?["short_id"]) == "0123abcd"
                && S(p.Outbound["tls"]?["utls"]?["fingerprint"]) == "chrome"
                && S(p.Outbound["tls"]?["server_name"]) == "www.microsoft.com"
                && p.Outbound["transport"] == null);
        Ok($"vless://{uuid}@1.2.3.4:443?security=reality&pbk={pbk}&sni=a.com", "vless reality без fp и sid → chrome, пустой sid",
           p => S(p.Outbound["tls"]?["utls"]?["fingerprint"]) == "chrome" && S(p.Outbound["tls"]?["reality"]?["short_id"]) == "");
        Ok($"VLESS://{uuid}@1.2.3.4:443?security=reality&pbk={pbk}  \n", "схема в верхнем регистре и мусор по краям", p => S(p.Outbound["server"]) == "1.2.3.4");
        Ok($"vless://{uuid}@[2001:db8::1]:8443?security=tls&sni=v6.example#v6", "vless IPv6-адрес",
           p => S(p.Outbound["server"]) == "2001:db8::1" && p.Outbound["server_port"].IntValue() == 8443);
        Ok($"vless://{uuid}@h.example:443?type=ws&security=tls&path=%2Fws%3Fed%3D2048&host=cdn.example&sni=cdn.example", "vless ws+tls, путь закодирован",
           p => S(p.Outbound["transport"]?["type"]) == "ws" && S(p.Outbound["transport"]?["path"]) == "/ws?ed=2048"
                && S(p.Outbound["transport"]?["headers"]?["Host"]) == "cdn.example");
        Ok($"vless://{uuid}@h.example:443?type=grpc&serviceName=gun&security=tls", "vless grpc", p => S(p.Outbound["transport"]?["service_name"]) == "gun");
        Ok($"vless://{uuid}@h.example:80?type=httpupgrade&path=/up&host=h.example", "vless httpupgrade без TLS",
           p => S(p.Outbound["transport"]?["type"]) == "httpupgrade" && p.Outbound["tls"] == null);
        Ok($"vless://{uuid}@h.example:443?type=h2&security=tls&host=a.example,b.example&path=/h2", "vless h2 → http-транспорт с host-списком",
           p => S(p.Outbound["transport"]?["type"]) == "http" && T.Eq(p.Outbound["transport"]?["host"], T.Arr("a.example", "b.example")));
        Ok($"vless://{uuid}@h.example:443?security=tls&alpn=h2,http/1.1&allowInsecure=1", "vless tls: alpn и insecure",
           p => T.Eq(p.Outbound["tls"]?["alpn"], T.Arr("h2", "http/1.1")) && p.Outbound["tls"]?["insecure"].IsTrue() == true);
        Ok($"vless://{uuid}@h.example:443#%F0%9F%87%A9%F0%9F%87%AA%20Frankfurt", "имя с флагом-эмодзи", p => p.Name == "🇩🇪 Frankfurt");
        Ok($"vless://{uuid}@h.example:443/?security=none", "слэш перед query", p => p.Outbound["server_port"].IntValue() == 443);
        Fails($"vless://{uuid}@1.2.3.4?security=reality&pbk={pbk}", "vless без порта");
        Fails("vless://@1.2.3.4:443", "vless без UUID");
        Fails($"vless://{uuid}@1.2.3.4:443?security=reality", "reality без pbk", "pbk");
        Fails($"vless://{uuid}@1.2.3.4:8443?type=xhttp&security=reality&pbk={pbk}", "XHTTP", "xhttp");
        Fails($"vless://{uuid}@1.2.3.4:8443?type=splithttp", "SplitHTTP", "splithttp");
        Fails($"vless://{uuid}@1.2.3.4:abc", "порт не число");

        // Trojan
        Ok("trojan://p%40ss@t.example:443?sni=t.example#TR", "trojan: TLS по умолчанию, пароль с @",
           p => S(p.Outbound["password"]) == "p@ss" && p.Outbound["tls"]?["enabled"].IsTrue() == true);
        Ok("trojan://pass@t.example:443?type=ws&path=/tr&security=tls&allowInsecure=1", "trojan ws, insecure",
           p => S(p.Outbound["transport"]?["type"]) == "ws" && p.Outbound["tls"]?["insecure"].IsTrue() == true);

        // VMess
        static string Vmess(string json) => "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Ok(Vmess("""{"v":"2","ps":"VM","add":"9.9.9.9","port":"443","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","aid":"0","net":"ws","path":"/ws","host":"h.example","tls":"tls","sni":"s.example"}"""), "vmess ws+tls, порт строкой",
           p => p.Name == "VM" && p.Outbound["server_port"].IntValue() == 443 && S(p.Outbound["tls"]?["server_name"]) == "s.example");
        Ok(Vmess("""{"add":"9.9.9.9","port":10086,"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","aid":0,"net":"tcp","scy":"chacha20-poly1305"}"""), "vmess tcp, порт числом, без имени",
           p => p.Name == "9.9.9.9" && S(p.Outbound["security"]) == "chacha20-poly1305" && p.Outbound["tls"] == null);
        Ok(Vmess("""{"add":"9.9.9.9","port":"443","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","net":"grpc","path":"svc","tls":"tls"}"""), "vmess grpc",
           p => S(p.Outbound["transport"]?["service_name"]) == "svc");
        var urlSafe = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"add":"9.9.9.9","port":"8080","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","ps":"???>>>"}"""))
            .Replace('+', '-').Replace('/', '_').Replace("=", "");
        Ok("vmess://" + urlSafe, "vmess URL-safe base64 без паддинга", p => p.Name == "???>>>");
        Fails("vmess://not-base64!!", "vmess мусор");

        // Shadowsocks
        Ok("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.2.3.4:8388#SS", "ss SIP002 base64",
           p => S(p.Outbound["method"]) == "aes-256-gcm" && S(p.Outbound["password"]) == "password");
        Ok("ss://2022-blake3-aes-128-gcm:MTIzNDU2Nzg5MDEy%2BzQ1Ng%3D%3D@5.6.7.8:443#x", "ss 2022, открытый userinfo",
           p => S(p.Outbound["password"]) == "MTIzNDU2Nzg5MDEy+zQ1Ng==");
        var legacy = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:p:ss@w0rd@ss.example:8389"));
        Ok($"ss://{legacy}#Legacy", "ss старый формат целиком в base64, пароль с : и @",
           p => S(p.Outbound["password"]) == "p:ss@w0rd" && S(p.Outbound["server"]) == "ss.example" && p.Outbound["server_port"].IntValue() == 8389);
        Ok("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@[2001:db8::2]:8388", "ss IPv6", p => S(p.Outbound["server"]) == "2001:db8::2");
        Ok("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.2.3.4:8388/?plugin=obfs-local%3Bobfs%3Dhttp%3Bobfs-host%3Dbing.com#obfs", "ss с плагином obfs",
           p => S(p.Outbound["plugin"]) == "obfs-local" && S(p.Outbound["plugin_opts"]) == "obfs=http;obfs-host=bing.com");
        Fails("ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.2.3.4:8388/?plugin=kcptun", "ss с неподдерживаемым плагином", "kcptun");

        // Hysteria2
        Ok("hy2://secret@7.7.7.7:8443?sni=a.example&obfs=salamander&obfs-password=xx#HY", "hysteria2 obfs",
           p => S(p.Outbound["obfs"]?["password"]) == "xx" && S(p.Outbound["tls"]?["server_name"]) == "a.example");
        Ok("hysteria2://secret@7.7.7.7?insecure=1", "hysteria2 без порта → 443",
           p => p.Outbound["server_port"].IntValue() == 443 && p.Outbound["tls"]?["insecure"].IsTrue() == true);
        Ok("hy2://secret@h.example:443,20000-30000/?sni=h.example", "hysteria2 port hopping",
           p => p.Outbound["server_port"].IntValue() == 443 && T.Eq(p.Outbound["server_ports"], T.Arr("20000:30000")));
        Ok("hy2://secret@[2001:db8::3]:9443", "hysteria2 IPv6", p => S(p.Outbound["server"]) == "2001:db8::3" && p.Outbound["server_port"].IntValue() == 9443);

        // TUIC
        Ok($"tuic://{uuid}:pw@8.8.8.8:443?congestion_control=bbr&alpn=h3&sni=t.example#TU", "tuic",
           p => S(p.Outbound["congestion_control"]) == "bbr" && S(p.Outbound["password"]) == "pw");
        Fails($"tuic://{uuid}@8.8.8.8:443", "tuic без пароля");

        // Garbage
        Fails("wireguard://abc", "неизвестная схема", "не поддерживается");
        Fails("hello world", "просто текст");
        Fails("", "пустая строка");

        T.Section("Ссылки: каждый разобранный outbound проходит sing-box check");
        for (var i = 0; i < parsed.Count; i++)
        {
            var (name, ob) = parsed[i];
            var p = new Profile();
            p.Servers.Add(new Server(name, "S", "#fff", ob, $"s{i}"));
            p.Settings.DefaultTarget = $"s{i}";
            var (good, output) = T.SingBoxCheck(new ConfigBuilder(p).Data(), $"link{i}");
            T.Check(good, name, output);
        }
    }
}
