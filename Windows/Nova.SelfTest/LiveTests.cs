using System.Text;
using System.Text.Json.Nodes;
using Nova.Core;

namespace Nova.SelfTest;

static class LiveTests
{
    /// Routing + kill switch against an unreachable server, through a local mixed proxy (no admin rights).
    public static async Task Routing()
    {
        T.Section("Живая маршрутизация: мёртвый сервер, kill switch, «всё остальное»");
        const int proxyPort = 17891;
        var live = new Profile();
        live.Settings.ClashPort = 19291;
        live.Settings.DefaultTarget = Target.Direct;
        live.Servers = [new Server("Мёртвый", "XX", "#FF5C6C", T.Obj("""{"type":"shadowsocks","server":"127.0.0.1","server_port":9,"method":"aes-256-gcm","password":"x"}"""), "dead")];
        live.DomainRules =
        [
            new(DomainRuleKind.Domain, "example.net", Target.Direct),
            new(DomainRuleKind.Domain, "example.com", "dead"),
            new(DomainRuleKind.Keyword, "iana", "dead"),
            new(DomainRuleKind.Domain, "example.org", "dead") { Enabled = false },
        ];

        var core = new LocalCore(T.SingBox, Path.Combine(T.Work, "live"));
        try
        {
            core.Apply(new ConfigBuilder(live) { Inbound = new ConfigBuilder.InboundKind.Mixed(proxyPort) }.Data());
            core.Start();
        }
        catch (CoreException e)
        {
            T.Check(false, "ядро стартует", e.Message);
            return;
        }
        try
        {
            var api = new ClashApi(live.Settings.ClashPort, live.ClashSecret);
            T.Check(await T.WaitFor(api.IsAlive), "Clash API отвечает");
            var intruder = new ClashApi(live.Settings.ClashPort, "wrong");
            T.Check(await intruder.Selected("via-dead") == null, "Clash API отвергает чужой секрет");
            var refused = false;
            try { await intruder.Select("via-dead", "direct"); } catch (ClashApi.ApiException) { refused = true; }
            T.Check(refused, "чужой секрет не может переключить селектор");

            T.Check(T.Fetch("https://example.net", proxyPort), "домен → direct работает");
            T.Check(!T.Fetch("https://example.com", proxyPort), "домен → мёртвый сервер: заблокировано (kill switch)");
            T.Check(!T.Fetch("https://www.iana.org", proxyPort), "ключевое слово → мёртвый сервер: заблокировано");
            T.Check(T.Fetch("https://example.org", proxyPort), "выключенное правило не действует");

            var monitor = new HealthMonitor(api);
            await monitor.Tick(["dead"], killSwitch: false);
            T.Check(monitor.Health["dead"] is { IsDown: false, Failures: 1 }, "один провал ещё не «упал» (защита от ложных срабатываний)");
            T.Check(await api.Selected("via-dead") == "srv-dead", "после одного провала селектор не трогаем");
            await monitor.Tick(["dead"], killSwitch: false);
            T.Check(monitor.Health["dead"].IsDown, "два провала подряд → сервер упал");
            T.Check(await api.Selected("via-dead") == "direct", "kill switch выкл → селектор на direct");
            T.Check(T.Fetch("https://example.com", proxyPort), "kill switch выкл → трафик идёт напрямую");
            await monitor.Tick(["dead"], killSwitch: true);
            T.Check(await api.Selected("via-dead") == "srv-dead", "kill switch вкл → селектор вернулся на сервер");
            T.Check(!T.Fetch("https://example.com", proxyPort), "kill switch вкл → снова заблокировано");
            await monitor.Tick([], killSwitch: true);
            T.Check(monitor.Health.Count == 0, "удалённые серверы пропадают из мониторинга");

            T.Check(T.Fetch("https://example.org", proxyPort), "всё остальное → direct");
            await api.Select(ConfigBuilder.DefaultRouteTag, ConfigBuilder.SelectorTag("dead"));
            T.Check(!T.Fetch("https://example.org", proxyPort), "всё остальное переключено на лету → заблокировано");
            await api.Select(ConfigBuilder.DefaultRouteTag, ConfigBuilder.DirectTag);
            T.Check(T.Fetch("https://example.org", proxyPort), "и обратно на direct без перезапуска");

            ClashApi.Connections? conns = null;
            try { conns = await api.GetConnections(); } catch (ClashApi.ApiException) { }
            T.Check(conns != null, "список соединений читается");
            var traffic = false;
            using (var cts = new CancellationTokenSource(5000))
            {
                try { await foreach (var _ in api.TrafficStream(cts.Token)) { traffic = true; break; } }
                catch (OperationCanceledException) { }
            }
            T.Check(traffic, "поток трафика читается");
            await api.CloseAllConnections();
            ClashApi.Connections? after = null;
            try { after = await api.GetConnections(); } catch (ClashApi.ApiException) { }
            T.Check((after?.List ?? []).Count == 0, "закрытие всех соединений работает");

            core.Stop();
            T.Check(!await api.IsAlive(), "после остановки ядра API недоступен (приложение это заметит)");
        }
        finally
        {
            core.Stop();
        }
    }

    /// End-to-end with real protocol servers on localhost: share link → parser → config →
    /// traffic actually arrives at the right server. Then kill switch recovery.
    public static async Task Protocols()
    {
        T.Section("Сквозной тест: настоящие серверы 6 протоколов на localhost");
        const string uuid = "6f1f0f3e-9a0e-4b7c-9c1e-2b8f1c7e5a11";
        const string ssPass = "c3VwZXItc2VjcmV0LXBhc3N3b3Jk";

        // Reality keys and a self-signed cert for the TLS-based servers.
        var (_, kp) = Shell.Run(T.SingBox, ["generate", "reality-keypair"]);
        string? Key(string prefix) => kp.Split('\n').FirstOrDefault(l => l.StartsWith(prefix))?.Split(' ').Last().Trim();
        var priv = Key("PrivateKey:");
        var pub = Key("PublicKey:");
        var (_, pem) = Shell.Run(T.SingBox, ["generate", "tls-keypair", "nova.test"]);
        const string keyEndMark = "-----END PRIVATE KEY-----";
        var keyEnd = pem.IndexOf(keyEndMark, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(priv) || string.IsNullOrEmpty(pub) || keyEnd < 0)
        {
            T.Check(false, "ключи для тестовых серверов", kp + pem);
            return;
        }
        var certDir = Path.Combine(T.Work, "certs");
        Directory.CreateDirectory(certDir);
        var keyPath = Path.Combine(certDir, "key.pem");
        var certPath = Path.Combine(certDir, "cert.pem");
        File.WriteAllText(keyPath, pem[..(keyEnd + keyEndMark.Length)] + "\n");
        File.WriteAllText(certPath, pem[(keyEnd + keyEndMark.Length)..].Trim() + "\n");
        JsonObject Tls() => new() { ["enabled"] = true, ["server_name"] = "nova.test", ["certificate_path"] = certPath, ["key_path"] = keyPath };

        var serverConfig = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "info" },
            ["inbounds"] = new JsonArray(
                new JsonObject { ["type"] = "shadowsocks", ["tag"] = "ss-in", ["listen"] = "127.0.0.1", ["listen_port"] = 18388, ["method"] = "aes-256-gcm", ["password"] = ssPass },
                new JsonObject { ["type"] = "vmess", ["tag"] = "vmess-in", ["listen"] = "127.0.0.1", ["listen_port"] = 18389, ["users"] = new JsonArray(new JsonObject { ["uuid"] = uuid, ["alterId"] = 0 }) },
                new JsonObject
                {
                    ["type"] = "vless", ["tag"] = "vless-in", ["listen"] = "127.0.0.1", ["listen_port"] = 18443,
                    ["users"] = new JsonArray(new JsonObject { ["uuid"] = uuid, ["flow"] = "xtls-rprx-vision" }),
                    ["tls"] = new JsonObject
                    {
                        ["enabled"] = true, ["server_name"] = "www.apple.com",
                        ["reality"] = new JsonObject
                        {
                            ["enabled"] = true, ["handshake"] = new JsonObject { ["server"] = "www.apple.com", ["server_port"] = 443 },
                            ["private_key"] = priv, ["short_id"] = T.Arr("0123abcd"),
                        },
                    },
                },
                new JsonObject { ["type"] = "trojan", ["tag"] = "trojan-in", ["listen"] = "127.0.0.1", ["listen_port"] = 18444, ["users"] = new JsonArray(new JsonObject { ["password"] = "tr-pass" }), ["tls"] = Tls() },
                new JsonObject { ["type"] = "hysteria2", ["tag"] = "hy2-in", ["listen"] = "127.0.0.1", ["listen_port"] = 18445, ["users"] = new JsonArray(new JsonObject { ["password"] = "hy-pass" }), ["tls"] = Tls() },
                new JsonObject
                {
                    ["type"] = "tuic", ["tag"] = "tuic-in", ["listen"] = "127.0.0.1", ["listen_port"] = 18446,
                    ["users"] = new JsonArray(new JsonObject { ["uuid"] = uuid, ["password"] = "tu-pass" }), ["congestion_control"] = "bbr",
                    ["tls"] = new JsonObject { ["enabled"] = true, ["server_name"] = "nova.test", ["alpn"] = T.Arr("h3"), ["certificate_path"] = certPath, ["key_path"] = keyPath },
                }),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
        };

        var server = new TestServer(serverConfig, "multi");
        try
        {
            await Task.Delay(1200);
            T.Check(server.IsRunning, "тестовые серверы стартуют", server.LogText);

            var vmessLink = "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"v":"2","ps":"VM","add":"127.0.0.1","port":"18389","id":"{{uuid}}","aid":"0","net":"tcp"}"""));
            (string Id, string Link, string Domain, string Inbound)[] cases =
            [
                ("vl", $"vless://{uuid}@127.0.0.1:18443?type=tcp&security=reality&pbk={pub}&sid=0123abcd&sni=www.apple.com&fp=chrome&flow=xtls-rprx-vision#VLESS", "example.com", "vless-in"),
                ("ss", "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes($"aes-256-gcm:{ssPass}")) + "@127.0.0.1:18388#SS", "example.net", "ss-in"),
                ("vm", vmessLink, "example.org", "vmess-in"),
                ("tr", "trojan://tr-pass@127.0.0.1:18444?sni=nova.test&allowInsecure=1#TR", "www.cloudflare.com", "trojan-in"),
                ("hy", "hy2://hy-pass@127.0.0.1:18445?sni=nova.test&insecure=1#HY", "www.wikipedia.org", "hy2-in"),
                ("tu", $"tuic://{uuid}:tu-pass@127.0.0.1:18446?sni=nova.test&insecure=1&alpn=h3&congestion_control=bbr#TU", "www.gstatic.com", "tuic-in"),
            ];

            var p = new Profile();
            p.Settings.ClashPort = 19292;
            p.Settings.DefaultTarget = Target.Direct;
            foreach (var c in cases)
            {
                LinkParser.Parsed parsed;
                try { parsed = LinkParser.Parse(c.Link); }
                catch (LinkParser.ParseException e)
                {
                    T.Check(false, $"ссылка {c.Id} разбирается", e.Message);
                    continue;
                }
                p.Servers.Add(new Server(parsed.Name, c.Id.ToUpperInvariant(), "#8B7CFF", parsed.Outbound, c.Id));
                p.DomainRules.Add(new DomainRule(DomainRuleKind.Domain, c.Domain, c.Id));
            }

            const int proxyPort = 17892;
            var core = new LocalCore(T.SingBox, Path.Combine(T.Work, "e2e"));
            try
            {
                core.Apply(new ConfigBuilder(p) { Inbound = new ConfigBuilder.InboundKind.Mixed(proxyPort) }.Data());
                core.Start();
            }
            catch (CoreException e)
            {
                T.Check(false, "клиент стартует", e.Message);
                return;
            }
            try
            {
                var api = new ClashApi(p.Settings.ClashPort, p.ClashSecret);
                T.Check(await T.WaitFor(api.IsAlive), "клиент поднялся");

                // Warm-up: the first request through each server pays for its cold DNS / Reality handshake.
                var monitor = new HealthMonitor(api);
                var ids = cases.Select(c => c.Id).ToList();
                await monitor.Tick(ids, killSwitch: true);

                static bool Arrived(string log, string inbound, string domain) =>
                    log.Split('\n').Any(l => l.Contains($"[{inbound}]: ") && l.Contains($"inbound connection to {domain}:443"));

                foreach (var c in cases)
                {
                    var ok = T.Fetch($"https://{c.Domain}", proxyPort, 15) || T.Fetch($"https://{c.Domain}", proxyPort, 15);
                    var arrived = Arrived(server.LogText, c.Inbound, c.Domain);
                    T.Check(ok && arrived, $"{c.Inbound.Replace("-in", "")}: {c.Domain} ушёл именно на свой сервер", $"запрос={ok} в логе сервера={arrived}");
                }

                var h = await monitor.Tick(ids, killSwitch: true);
                if (ids.Any(id => h[id].DelayMs == null)) h = await monitor.Tick(ids, killSwitch: true); // one retry, like the real monitor
                foreach (var c in cases)
                    T.Check(h[c.Id].DelayMs != null, $"задержка через {c.Id} измеряется ({(h[c.Id].DelayMs is { } ms ? $"{ms} мс" : "нет")})");

                T.Section("Kill switch: сервер падает и поднимается");
                server.Stop();
                await Task.Delay(500);
                for (var i = 0; i < HealthMonitor.FailuresToMarkDown; i++) await monitor.Tick(["vl"], killSwitch: true);
                T.Check(monitor.Health["vl"].IsDown, "падение сервера замечено");
                T.Check(!T.Fetch("https://example.com", proxyPort, 6), "kill switch вкл: трафик упавшего сервера заблокирован");
                await monitor.Tick(["vl"], killSwitch: false);
                T.Check(T.Fetch("https://example.com", proxyPort), "выключили kill switch → сразу пошло напрямую");

                server.Dispose();
                server = new TestServer(serverConfig, "multi-2");
                await Task.Delay(1200);
                // The app re-checks every ~10 s; allow a few rounds for the fresh server's cold DNS.
                var rounds = 0;
                do
                {
                    rounds++;
                    await monitor.Tick(["vl"], killSwitch: false);
                } while (monitor.Health["vl"].IsDown && rounds < 6);
                T.Check(!monitor.Health["vl"].IsDown, $"сервер снова доступен (проверок: {rounds})");
                T.Check(await api.Selected("via-vl") == "srv-vl", "селектор сам вернулся на сервер");
                T.Fetch("https://example.com", proxyPort, 15);
                T.Check(Arrived(server.LogText, "vless-in", "example.com"), "трафик снова идёт через сервер");
            }
            finally
            {
                core.Stop();
            }
        }
        finally
        {
            server.Dispose();
        }
    }
}
