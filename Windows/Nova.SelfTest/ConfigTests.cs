using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Nova.Core;

namespace Nova.SelfTest;

static class ConfigTests
{
    static string? S(JsonNode? n) => n.StringValue();

    public static void Run()
    {
        T.Section("Генератор: нормализация");
        (string, string)[] domains =
        [
            ("YouTube.COM", "youtube.com"), ("*.a.example", "a.example"), (".ru", "ru"), ("*.*.x.org", "x.org"),
            ("https://x.com/path?q=1#f", "x.com"), ("x.com:443", "x.com"), ("example.com.", "example.com"),
            ("  spaced.com \n", "spaced.com"), ("user@mail.example", "mail.example"), ("http://[::1]:80/", "[::1]"),
        ];
        foreach (var (raw, want) in domains)
        {
            var got = ConfigBuilder.NormalizeDomain(raw);
            T.Check(got == want, $"домен «{raw.Replace("\n", "\\n")}» → {want}", $"получено «{got}»");
        }
        foreach (var bad in new[] { "", "a b.com", "под домен.рф", "x..y", "[::1]", "a_b.com" })
            T.Check(!ConfigBuilder.IsValidDomain(ConfigBuilder.NormalizeDomain(bad)), $"невалидный домен «{bad}» отбрасывается");
        T.Check(ConfigBuilder.IsValidDomain("xn--80ak6aa92e.com"), "punycode-домен валиден");
        (string, string?)[] cidrs =
        [
            ("1.2.3.4", "1.2.3.4/32"), ("10.0.0.0/8", "10.0.0.0/8"), ("2001:db8::1", "2001:db8::1/128"),
            ("2001:db8::/32", "2001:db8::/32"), (" 8.8.8.8 ", "8.8.8.8/32"), ("abc.def", null), ("1.2.3.4/33", null),
            ("300.1.1.1", null), ("1.2.3.4/", null), ("1.2.3.4/8/9", null), ("", null), ("1.2.3", null), ("fe80::1%12", null),
        ];
        foreach (var (raw, want) in cidrs)
        {
            var got = ConfigBuilder.NormalizeCidr(raw);
            T.Check(got == want, $"IP «{raw}» → {want ?? "отброшен"}", $"получено {got ?? "null"}");
        }

        T.Section("Генератор: правила по приложениям (пути Windows)");
        (string, string)[] regexCases =
        [
            (@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"(?i)\\chrome\.exe$"),
            (@"C:\Users\alex\AppData\Local\AnthropicClaude\app-1.2.3\Claude.exe", @"(?i)\\Claude\.exe$"),
            (@"D:\Apps\C++ (beta) [x].exe", @"(?i)\\C\+\+ \(beta\) \[x\]\.exe$"),
            (@"C:\Program Files\Яндекс Музыка\Яндекс Музыка.EXE", @"(?i)\\Яндекс Музыка\.EXE$"),
            (@"C:\Tools\$weird^{1}|.exe", @"(?i)\\\$weird\^\{1\}\|\.exe$"),
            (@"C:\Program Files\Mozilla Firefox\", @"(?i)^C:\\Program Files\\Mozilla Firefox\\"),
            ("C:/Games/Steam", @"(?i)^C:\\Games\\Steam\\"),
        ];
        foreach (var (path, want) in regexCases)
        {
            var got = ConfigBuilder.AppPathRegexes(path);
            T.Check(got.Count == 1 && got[0] == want, $"regex для «{path}»", string.Join(" | ", got));
        }
        // sing-box uses Go RE2; for these patterns .NET Regex behaves the same.
        static bool Matches(string rx, string path) => Regex.IsMatch(path, rx);
        var claude = ConfigBuilder.AppPathRegexes(@"C:\Users\alex\AppData\Local\AnthropicClaude\app-1.2.3\Claude.exe")[0];
        T.Check(Matches(claude, @"C:\Users\alex\AppData\Local\AnthropicClaude\app-1.4.0\Claude.exe"), "Claude: после обновления (новая папка версии) правило работает");
        T.Check(Matches(claude, @"c:\users\alex\appdata\local\anthropicclaude\app-1.2.3\claude.exe"), "Claude: регистр пути не важен");
        T.Check(!Matches(claude, @"C:\Program Files\Claude Code\claude-code.exe") && !Matches(claude, @"C:\x\NotClaude.exe"), "Claude: не ловит похожие имена");
        var chrome = ConfigBuilder.AppPathRegexes(@"C:\Program Files\Google\Chrome\Application\chrome.exe")[0];
        T.Check(Matches(chrome, @"C:\Users\a\AppData\Local\Google\Chrome\Application\chrome.exe"), "Chrome: установка в профиль пользователя ловится");
        var folder = ConfigBuilder.AppPathRegexes(@"C:\Games\Steam")[0];
        T.Check(Matches(folder, @"C:\Games\Steam\steamapps\common\x\game.exe") && !Matches(folder, @"C:\Games\SteamTools\x.exe"), "папка: только её содержимое");

        T.Section("Генератор: семантика правил");
        var p = new Profile();
        p.Servers = [T.MakeServer("de"), T.MakeServer("ru", 8443)];
        p.DomainRules =
        [
            new(DomainRuleKind.Domain, "youtube.com", "de"),
            new(DomainRuleKind.Domain, "bad domain.com", "de"),
            new(DomainRuleKind.Ip, "not-an-ip", "de"),
            new(DomainRuleKind.Ip, "10.8.0.0/16", "ru"),
            new(DomainRuleKind.Keyword, "OpenAI", "de"),
            new(DomainRuleKind.Domain, "gone.example", "deleted-server"),
            new(DomainRuleKind.Domain, "baidu.com", Target.Direct),
            new(DomainRuleKind.Ruleset, "geosite-youtube, geosite-youtube ,geoip-cn", "de"),
            new(DomainRuleKind.Ruleset, "geosite-cn", Target.Direct),
            new(DomainRuleKind.Ruleset, "not-a-set", "de"),
            new(DomainRuleKind.Domain, "   ", "de"),
            new(DomainRuleKind.Domain, "disabled.example", "de") { Enabled = false },
        ];
        p.AppRules =
        [
            new("Telegram", @"C:\Users\a\AppData\Roaming\Telegram Desktop\Telegram.exe", "de"),
            new("Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "ru"),
            new("Empty", "", "de"),
            new("Off", @"C:\Off\Off.exe", "de") { Enabled = false },
        ];
        p.Settings.DefaultTarget = "ru";
        var c = new ConfigBuilder(p).Build();
        var rules = T.RouteRules(c);

        T.Check(S(rules[0]["action"]) == "sniff", "первое правило — sniff");
        T.Check(S(rules[1]["action"]) == "hijack-dns", "второе — перехват DNS");
        T.Check(rules[2]["ip_is_private"].IsTrue(), "третье — локальная сеть → direct");
        T.Check(S(T.RouteRule(c, "domain_suffix", "youtube.com")?["outbound"]) == "via-de", "youtube.com → via-de");
        T.Check(T.RouteRule(c, "domain_suffix", "bad domain.com") == null, "невалидный домен пропущен");
        T.Check(T.RouteRule(c, "ip_cidr", "not-an-ip") == null, "невалидный IP пропущен");
        T.Check(S(T.RouteRule(c, "ip_cidr", "10.8.0.0/16")?["outbound"]) == "via-ru", "подсеть → via-ru");
        T.Check(S(T.RouteRule(c, "domain_keyword", "openai")?["outbound"]) == "via-de", "ключевое слово в нижнем регистре");
        T.Check(S(T.RouteRule(c, "domain_suffix", "gone.example")?["outbound"]) == "direct", "правило на удалённый сервер → direct");
        T.Check(T.RouteRule(c, "domain_suffix", "disabled.example") == null, "выключенное правило не попадает в конфиг");
        T.Check(!rules.Any(r => (r["process_path_regex"]?.ToJsonString() ?? "").Contains("Off")), "выключенное приложение не попадает");
        T.Check(rules.Count(r => r["process_path_regex"] != null) == 2, "приложение с пустым путём пропущено");

        var rsRule = rules.FirstOrDefault(r => r["rule_set"] != null && S(r["outbound"]) == "via-de");
        T.Check(T.Eq(rsRule?["rule_set"], T.Arr("geosite-youtube", "geoip-cn")), "теги наборов: пробелы, дубли, мусор убраны", rsRule?["rule_set"]?.ToJsonString() ?? "null");
        var tags = (c["route"]?["rule_set"] as JsonArray)?.Select(x => S(x?["tag"])).ToList();
        T.Check(tags != null && tags.SequenceEqual(["geosite-youtube", "geoip-cn", "geosite-cn"]), "rule_set без повторов", string.Join(",", tags ?? []));

        // Order: all domain rules come before any app rule.
        var lastDomain = rules.FindLastIndex(r => r["domain_suffix"] != null || r["rule_set"] != null || r["ip_cidr"] != null || r["domain_keyword"] != null);
        var firstApp = rules.FindIndex(r => r["process_path_regex"] != null);
        T.Check(firstApp > 0 && lastDomain < firstApp, "все правила доменов стоят раньше правил приложений (вариант А)");
        var tg = rules.FirstOrDefault(r => (r["process_path_regex"]?.ToJsonString() ?? "").Contains("Telegram"));
        T.Check(S(tg?["outbound"]) == "via-de", "Telegram → via-de");
        var edge = rules.FirstOrDefault(r => (r["process_path_regex"]?.ToJsonString() ?? "").Contains("msedge"));
        T.Check(S(edge?["outbound"]) == "via-ru", "Edge → via-ru");

        T.Check(S(c["route"]?["final"]) == "default-route", "final → селектор default-route");
        var outbounds = (c["outbounds"] as JsonArray)!.OfType<JsonObject>().ToList();
        var dr = outbounds.FirstOrDefault(o => S(o["tag"]) == "default-route");
        T.Check(S(dr?["default"]) == "via-ru", "default-route по умолчанию указывает на RU");
        T.Check(T.Eq(dr?["outbounds"], T.Arr("via-de", "via-ru", "direct")), "default-route содержит все серверы и direct");
        foreach (var id in new[] { "de", "ru" })
        {
            var sel = outbounds.FirstOrDefault(o => S(o["tag"]) == $"via-{id}");
            T.Check(T.Eq(sel?["outbounds"], T.Arr($"srv-{id}", "direct")) && S(sel?["default"]) == $"srv-{id}", $"селектор via-{id} = [srv-{id}, direct]");
            T.Check(outbounds.Any(o => S(o["tag"]) == $"srv-{id}" && S(o["type"]) == "shadowsocks"), $"outbound srv-{id} на месте");
        }
        T.Check(p.Servers[0].Outbound["tag"] == null, "профиль не портится при сборке конфига (tag только в копии)");

        // DNS: fake IP for A/AAAA, local for DIRECT domains and LAN names.
        var dns = T.DnsRules(c);
        T.Check(S(dns[0]["server"]) == "dns-local" && dns[0]["domain_suffix"].StringList().Contains("local"), "DNS: локальные имена → системный DNS");
        T.Check(T.Eq(dns[^1]["query_type"], T.Arr("A", "AAAA")) && S(dns[^1]["server"]) == "dns-fake", "DNS: A/AAAA → FakeIP последним правилом");
        T.Check(dns.Any(d => T.Eq(d["domain_suffix"], T.Arr("baidu.com")) && S(d["server"]) == "dns-local"), "DNS: DIRECT-домен резолвится по-настоящему");
        T.Check(dns.Any(d => T.Eq(d["rule_set"], T.Arr("geosite-cn")) && S(d["server"]) == "dns-local"), "DNS: DIRECT-набор geosite резолвится по-настоящему");
        T.Check(!dns.Any(d => T.Eq(d["domain_suffix"], T.Arr("youtube.com"))), "DNS: домен через сервер идёт в FakeIP");
        T.Check(!dns.Any(d => (d["rule_set"]?.ToJsonString() ?? "").Contains("geoip")), "DNS: geoip-наборы в DNS-правила не попадают");
        T.Check(S(c["dns"]?["final"]) == "dns-local", "DNS final — системный");
        T.Check(c["experimental"]?["cache_file"]?["store_fakeip"].IsTrue() == true, "FakeIP переживает перезапуск");
        var tun = (c["inbounds"] as JsonArray)?[0];
        T.Check(S(tun?["type"]) == "tun" && S(tun?["interface_name"]) == "Nova" && tun?["strict_route"].IsTrue() == true,
                "TUN: адаптер «Nova», strict_route (без утечек DNS через другие адаптеры)");

        var (good, output) = T.SingBoxCheck(new ConfigBuilder(p).Data(), "semantics");
        T.Check(good, "этот профиль проходит sing-box check", output);

        T.Section("Генератор: локальные наборы правил");
        var dir = Path.Combine(T.Work, "sets-partial");
        Directory.CreateDirectory(dir);
        // Only geosite-youtube "downloaded": rules for missing sets must be dropped, not break the config.
        var real = Path.Combine(T.Work, "rule-sets", "geosite-telegram.srs");
        if (File.Exists(real)) File.Copy(real, Path.Combine(dir, "geosite-youtube.srs"), true);
        var lc = new ConfigBuilder(p) { RuleSetDirectory = dir }.Build();
        var sets = (lc["route"]?["rule_set"] as JsonArray)?.OfType<JsonObject>().ToList();
        T.Check(sets is { Count: 1 } && S(sets[0]["type"]) == "local" && S(sets[0]["tag"]) == "geosite-youtube", "объявлен только скачанный набор, type local");
        T.Check(!T.RouteRules(lc).Any(r => (r["rule_set"]?.ToJsonString() ?? "").Contains("geosite-cn")), "правило с нескачанным набором пропущено");
        var (good2, output2) = T.SingBoxCheck(new ConfigBuilder(p) { RuleSetDirectory = dir }.Data(), "partial-sets");
        T.Check(good2, "конфиг с частью наборов проходит check", output2);

        T.Section("Генератор: профиль и модель");
        var legacy = """{"version":1,"servers":[],"domainRules":[],"appRules":[],"settings":{"killSwitch":false,"remoteDNS":"8.8.8.8"},"clashSecret":"s"}""";
        Profile? decoded = null;
        try { decoded = Profile.FromJson(legacy); } catch (JsonException) { }
        T.Check(decoded?.Settings.KillSwitch == false && decoded.Settings.ClashPort == 19190, "старый профиль с лишними и недостающими полями читается");
        var threw = false;
        try { Profile.FromJson("""{"settings":{}}"""); } catch (JsonException) { threw = true; }
        T.Check(threw, "профиль без обязательных полей не читается (уйдёт в резервную копию)");
        T.Check(Profile.FromJson(p.ToJson()).SameAs(p), "профиль сохраняется и читается без потерь");
        T.Check(new Profile().ClashSecret != new Profile().ClashSecret, "у каждого профиля свой секрет Clash API");
        var mac = """{"version":1,"servers":[{"id":"ab12cd34","name":"Германия","code":"DE","colorHex":"#8B7CFF","outbound":{"type":"vless","server":"h","server_port":443,"uuid":"u"}}],"domainRules":[{"id":"X","kind":"ruleset","value":"geosite-cn","target":"direct","enabled":true}],"appRules":[],"settings":{"defaultTarget":"ab12cd34"},"clashSecret":"0123456789abcdef0123"}""";
        Profile? fromMac = null;
        try { fromMac = Profile.FromJson(mac); } catch (JsonException) { }
        T.Check(fromMac?.Servers[0].Name == "Германия" && fromMac.DomainRules[0].Kind == DomainRuleKind.Ruleset, "profile.json из macOS-версии читается как есть");
        T.Check(p.ToJson().Contains("\"Telegram\"") && !new Profile { Servers = [new Server("Германия", "DE", "#fff", new JsonObject(), "g")] }.ToJson().Contains("\\u"),
                "profile.json читаемый: кириллица без \\u-экранирования");
    }

    public static void HelperTests()
    {
        T.Section("Вспомогательная логика");
        (string, string)[] codes = [("🇩🇪 Frankfurt", "DE"), ("🇷🇺", "RU"), ("Germany-1", "GE"), ("Германия", "ГЕ"), ("", "S"), ("123", "S"), ("x", "X")];
        foreach (var (name, want) in codes)
            T.Check(Naming.SuggestedCode(name) == want, $"метка для «{name}» → {want}", Naming.SuggestedCode(name));
        T.Check(Naming.DisplayName(@"C:\Program Files\Telegram Desktop\Telegram.exe") == "Telegram", "имя программы без .exe");
        T.Check(Naming.DisplayName(@"C:\Games\Steam\") == "Steam", "имя папки");
        T.Check(Naming.UsesSharedWebView(@"C:\Program Files\WindowsApps\Microsoft.OutlookForWindows\olk.exe") && !Naming.UsesSharedWebView(@"C:\x\chrome.exe"),
                "новый Outlook помечается как WebView2");

        string[] ids = ["de", "ru"];
        T.Check(ConnectionInfo.Target(["srv-de", "via-de"], ids) == "de", "цепочка через сервер → сервер");
        T.Check(ConnectionInfo.Target(["srv-ru", "via-ru", "default-route"], ids) == "ru", "через «всё остальное» → сервер");
        T.Check(ConnectionInfo.Target(["direct", "via-de"], ids) == "de", "фолбэк kill switch → считается за сервер");
        T.Check(ConnectionInfo.Target(["direct", "default-route"], ids) == Target.Direct, "напрямую → direct");
        T.Check(ConnectionInfo.Target(["srv-gone", "via-gone"], ids) == Target.Direct, "удалённый сервер → direct");

        static List<ClashApi.Connection> Conns(IEnumerable<string?> paths)
        {
            var items = string.Join(",", paths.Select((p, i) =>
            {
                var path = p == null ? "" : $",\"processPath\":{JsonSerializer.Serialize(p)}";
                return "{\"id\":\"" + i + "\",\"upload\":0,\"download\":0,\"chains\":[\"direct\"],\"metadata\":{\"network\":\"tcp\"" + path + "}}";
            }));
            return ClashApi.Connections.Parse($$"""{"downloadTotal":0,"uploadTotal":0,"connections":[{{items}}]}""").List ?? [];
        }
        T.Check(ConnectionInfo.ProcessLookupBroken(Conns(Enumerable.Repeat<string?>(null, 8))), "8 соединений без процесса → тревога");
        T.Check(!ConnectionInfo.ProcessLookupBroken(Conns(Enumerable.Repeat<string?>(null, 4))), "мало соединений → без тревоги");
        T.Check(!ConnectionInfo.ProcessLookupBroken(Conns(Enumerable.Repeat<string?>(null, 7).Append(@"C:\Windows\System32\curl.exe"))), "хоть один процесс найден → без тревоги");
        var nullList = false;
        try { nullList = ClashApi.Connections.Parse("""{"downloadTotal":1,"uploadTotal":2,"connections":null}""") != null; } catch (JsonException) { }
        T.Check(nullList, "пустой список соединений (null) читается");

        SystemNetwork.Adapter A(string name, string desc, NetworkInterfaceType type = NetworkInterfaceType.Unknown, bool up = true) => new(name, desc, type, up);
        var adapters = new[]
        {
            A("Ethernet", "Intel(R) Ethernet Connection I219-V", NetworkInterfaceType.Ethernet),
            A("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz", NetworkInterfaceType.Wireless80211),
            A("Nova", "Nova", NetworkInterfaceType.Unknown),
            A("wg-office", "WireGuard Tunnel"),
            A("Local Area Connection", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, up: false),
            A("Corp VPN", "WAN Miniport (IKEv2)", NetworkInterfaceType.Ppp),
            A("Teredo", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel),
            A("Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback),
            A("Mihomo", "Wintun Userspace Tunnel"),
        };
        var found = SystemNetwork.ParseConnected(adapters);
        T.Check(found.SequenceEqual(["wg-office", "Corp VPN", "Mihomo"]), "сторонние VPN: WireGuard, PPP, чужой Wintun; без своего «Nova», Teredo и выключенного TAP", string.Join(", ", found));
        Console.WriteLine($"    сейчас подключены другие VPN: [{string.Join(", ", SystemNetwork.ConnectedVpns())}]");

        T.Section("Профиль на диске");
        var dir = Path.Combine(T.Work, "profile-store");
        ProfileStore.DirectoryOverride = dir;
        try
        {
            T.Check(ProfileStore.Load().DomainRules.FirstOrDefault()?.Value == "geosite-cn,geoip-cn", "нет файла → стартовый профиль");
            var p = Profile.Starter();
            p.Servers = [T.MakeServer("de")];
            ProfileStore.Save(p);
            if (!OperatingSystem.IsWindows())
                T.Check(File.GetUnixFileMode(ProfileStore.ProfilePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "profile.json сохраняется с правами 600 (там ключи)");
            T.Check(ProfileStore.Load().SameAs(p), "сохранение и загрузка");
            File.WriteAllText(ProfileStore.ProfilePath, "{ это не json");
            var loaded = ProfileStore.Load();
            var backups = Directory.GetFiles(dir, "profile.broken-*");
            T.Check(loaded.Servers.Count == 0 && backups.Length == 1, "битый профиль → резервная копия + новый профиль");
            T.Check(ProfileStore.LoadError?.Contains("profile.broken-") == true, "пользователь увидит сообщение о битом профиле");
        }
        finally
        {
            ProfileStore.DirectoryOverride = null;
        }
    }

    /// The service-side allowlist: everything Nova generates passes, every attack from the audit is refused.
    public static void GuardTests()
    {
        T.Section("Защита службы: ConfigGuard");
        var setsDir = Path.Combine(T.Work, "guard-sets");
        Directory.CreateDirectory(setsDir);
        new RuleSetStore(setsDir).Seed(Path.Combine(Path.GetDirectoryName(T.SingBox)!, "rule-sets"));

        var p = Profile.Starter();
        p.Servers = [T.MakeServer("de"), T.MakeServer("ru", 8443)];
        p.DomainRules.Add(new(DomainRuleKind.Ruleset, "geosite-youtube", "de"));
        p.DomainRules.Add(new(DomainRuleKind.Domain, "x.com", "ru"));
        p.AppRules = [new("T", @"C:\Telegram\Telegram.exe", "de")];
        p.Settings.DefaultTarget = "de";
        var good = new ConfigBuilder(p) { RuleSetDirectory = setsDir }.Data();
        var goodErrors = ConfigGuard.Validate(good, setsDir);
        T.Check(goodErrors.Count == 0, "конфиг Nova проходит", string.Join("; ", goodErrors));
        T.Check(ConfigGuard.Validate(new ConfigBuilder(new Profile()).Data(), setsDir).Count == 0, "пустой профиль проходит");
        var verbose = p.Clone();
        verbose.Settings.VerboseLog = true;
        T.Check(ConfigGuard.Validate(new ConfigBuilder(verbose).Data(), setsDir).Count == 0, "подробный лог проходит");

        byte[] Mutated(Action<JsonObject> change)
        {
            var obj = (JsonObject)JsonNode.Parse(good)!;
            change(obj);
            return Encoding.UTF8.GetBytes(obj.ToJsonString());
        }
        JsonObject Clash(JsonObject o) => (JsonObject)o["experimental"]!["clash_api"]!;
        JsonObject FirstSet(JsonObject o) => (JsonObject)o["route"]!["rule_set"]![0]!;

        var evil = Path.Combine(setsDir, "evil.srs");
        File.Delete(evil);
        try { File.CreateSymbolicLink(evil, OperatingSystem.IsWindows() ? @"C:\Windows\System32\config\SAM" : "/etc/master.passwd"); }
        catch (Exception) { /* creating symlinks on Windows needs developer mode; the junction case below still runs */ }
        var junction = Path.Combine(T.Work, "guard-junction");
        try
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            Directory.CreateSymbolicLink(junction, setsDir);
        }
        catch (Exception) { }

        (string, Action<JsonObject>)[] attacks =
        [
            ("external_ui: / — раздача файлов от SYSTEM", o => Clash(o)["external_ui"] = "/"),
            ("external_ui_download_url — распаковка zip от SYSTEM", o => { Clash(o)["external_ui"] = "/tmp/x"; Clash(o)["external_ui_download_url"] = "https://e.vil/x.zip"; }),
            ("log.output — запись в любой файл", o => o["log"] = new JsonObject { ["level"] = "info", ["output"] = @"C:\Windows\System32\drivers\etc\hosts" }),
            ("Clash API на 0.0.0.0", o => Clash(o)["external_controller"] = "0.0.0.0:19190"),
            ("Clash API без секрета", o => Clash(o)["secret"] = ""),
            ("mixed-инбаунд на 0.0.0.0 — открытый прокси", o => ((JsonArray)o["inbounds"]!).Add(new JsonObject { ["type"] = "mixed", ["listen"] = "0.0.0.0", ["listen_port"] = 1080 })),
            ("инбаунд заменён на mixed", o => o["inbounds"] = new JsonArray(new JsonObject { ["type"] = "mixed", ["listen"] = "127.0.0.1", ["listen_port"] = 1080 })),
            ("tun с лишним полем", o => o["inbounds"]![0]!["platform"] = new JsonObject { ["http_proxy"] = new JsonObject { ["enabled"] = true } }),
            ("cache_file.path", o => o["experimental"]!["cache_file"] = new JsonObject { ["enabled"] = true, ["path"] = @"C:\x.db" }),
            ("tls.certificate_path в outbound", o => o["outbounds"]![0]!["tls"] = new JsonObject { ["enabled"] = true, ["certificate_path"] = @"C:\Windows\win.ini" }),
            ("ech.config_path глубоко внутри", o => o["outbounds"]![0]!["tls"] = new JsonObject { ["enabled"] = true, ["ech"] = new JsonObject { ["enabled"] = true, ["config_path"] = "/x" } }),
            ("набор правил вне папки Nova", o => FirstSet(o)["path"] = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/master.passwd"),
            ("набор правил через ..", o => FirstSet(o)["path"] = Path.Combine(setsDir, "..", "..", "..", "..", "etc", "x.srs")),
            ("набор правил — симлинк наружу", o => FirstSet(o)["path"] = evil),
            ("набор правил через junction/симлинк на папку", o => FirstSet(o)["path"] = Path.Combine(junction, "geosite-youtube.srs")),
            ("набор правил по UNC-пути", o => FirstSet(o)["path"] = @"\\attacker\share\geosite-youtube.srs"),
            ("набор правил — альтернативный поток NTFS", o => FirstSet(o)["path"] = Path.Combine(setsDir, "geosite-youtube.srs") + ":evil"),
            ("набор правил — не .srs", o => FirstSet(o)["path"] = Path.Combine(setsDir, "..", "guard-sets", "profile.json")),
            ("remote-набор по http", o => o["route"]!["rule_set"] = new JsonArray(new JsonObject { ["type"] = "remote", ["tag"] = "x", ["format"] = "binary", ["url"] = "http://e.vil/x.srs" })),
            ("неизвестный раздел верхнего уровня", o => o["services"] = new JsonArray(new JsonObject { ["type"] = "ssm-api" })),
            ("DNS-сервер с путём (dhcp/hosts)", o => ((JsonArray)o["dns"]!["servers"]!).Add(new JsonObject { ["type"] = "hosts", ["tag"] = "h", ["path"] = T.Arr(@"C:\Windows\System32\drivers\etc\hosts") })),
            ("outbound неизвестного типа", o => ((JsonArray)o["outbounds"]!).Add(new JsonObject { ["type"] = "tor", ["tag"] = "t" })),
        ];
        foreach (var (name, change) in attacks)
        {
            var errs = ConfigGuard.Validate(Mutated(change), setsDir);
            T.Check(errs.Count > 0, $"отклонено: {name}", "прошло без ошибок");
        }
        T.Check(ConfigGuard.Validate("not json"u8.ToArray(), setsDir).Count > 0, "отклонено: не JSON");
        T.Check(ConfigGuard.Validate("[]"u8.ToArray(), setsDir).Count > 0, "отклонено: не объект");
    }

    public static void SecurityFlagTests()
    {
        T.Section("Защита: allowInsecure и подробный лог");
        static Server? Srv(string link)
        {
            try { var p = LinkParser.Parse(link); return new Server(p.Name, "S", "#fff", p.Outbound); }
            catch (LinkParser.ParseException) { return null; }
        }
        T.Check(Srv("trojan://p@t.example:443?allowInsecure=1")?.SkipsCertificateCheck == true, "trojan с allowInsecure=1 помечен");
        T.Check(Srv("hy2://p@h.example:443?insecure=1")?.SkipsCertificateCheck == true, "hy2 с insecure=1 помечен");
        T.Check(Srv("trojan://p@t.example:443?sni=t.example")?.SkipsCertificateCheck == false, "обычный TLS не помечен");
        T.Check(Srv("vless://11111111-2222-3333-4444-555555555555@h:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0")?.SkipsCertificateCheck == false, "Reality не помечен");
        var p = new Profile();
        T.Check(S(new ConfigBuilder(p).Build()["log"]?["level"]) == "warn", "по умолчанию лог без доменов (warn)");
        p.Settings.VerboseLog = true;
        T.Check(S(new ConfigBuilder(p).Build()["log"]?["level"]) == "info", "подробный лог — info");
        var old = Profile.FromJson("""{"version":1,"servers":[],"domainRules":[],"appRules":[],"settings":{},"clashSecret":"0123456789abcdef0123"}""");
        T.Check(!old.Settings.VerboseLog, "старый профиль: подробный лог выключен");
    }

    public static async Task PipeTests()
    {
        T.Section("Протокол службы (named pipe)");
        using var ms = new MemoryStream();
        var payload = "{\"x\":1}"u8.ToArray();
        await PipeProtocol.WriteRequest(ms, "apply", payload, default);
        ms.Position = 0;
        var req = await PipeProtocol.ReadRequest(ms, default);
        T.Check(req is { Command: "apply" } r && r.Payload!.SequenceEqual(payload), "apply с конфигом доходит без искажений");
        T.Check(await Read("start\n") is { Command: "start", Payload: null }, "команда без данных");
        T.Check(await Read($"apply {PipeProtocol.MaxConfig + 1}\n") == null, "слишком большой конфиг отклоняется до чтения");
        T.Check(await Read("apply 100\nshort") == null, "обрезанный конфиг отклоняется");
        T.Check(await Read("apply -5\n") == null, "отрицательная длина отклоняется");
        T.Check(await Read(new string('a', 500)) == null, "строка без перевода отклоняется");
        using var reply = new MemoryStream();
        await PipeProtocol.WriteReply(reply, false, "sing-box отклонил конфиг", default);
        reply.Position = 0;
        var (ok, text) = await PipeProtocol.ReadReply(reply, default);
        T.Check(!ok && text == "sing-box отклонил конфиг", "ошибка службы доходит до приложения текстом");

        static async Task<(string Command, byte[]? Payload)?> Read(string raw)
        {
            using var s = new MemoryStream(Encoding.UTF8.GetBytes(raw));
            return await PipeProtocol.ReadRequest(s, default);
        }
    }
}
