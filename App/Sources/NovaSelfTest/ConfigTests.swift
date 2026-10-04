import Foundation
import NovaCore

func server(_ id: String, port: Int = 443) -> Server {
    Server(id: id, name: id.uppercased(), code: id.uppercased(), colorHex: "#8B7CFF",
           outbound: ["type": "shadowsocks", "server": .string("203.0.113.\(port % 250)"), "server_port": .int(port),
                      "method": "aes-256-gcm", "password": "x"])
}

/// Finds the route rule whose matcher key holds `value`.
func routeRule(_ config: JSONValue, key: String, value: String) -> JSONValue? {
    guard case .array(let rules)? = config["route"]?["rules"] else { return nil }
    return rules.first { r in
        if case .array(let vals)? = r[key] { return vals.contains(.string(value)) }
        return false
    }
}

func routeRules(_ config: JSONValue) -> [JSONValue] {
    if case .array(let rules)? = config["route"]?["rules"] { return rules }
    return []
}

func dnsRules(_ config: JSONValue) -> [JSONValue] {
    if case .array(let rules)? = config["dns"]?["rules"] { return rules }
    return []
}

func configTests() {
    T.section("Генератор: нормализация")
    let domains: [(String, String)] = [
        ("YouTube.COM", "youtube.com"), ("*.a.example", "a.example"), (".ru", "ru"), ("*.*.x.org", "x.org"),
        ("https://x.com/path?q=1#f", "x.com"), ("x.com:443", "x.com"), ("example.com.", "example.com"),
        ("  spaced.com \n", "spaced.com"), ("user@mail.example", "mail.example"), ("http://[::1]:80/", "[::1]"),
    ]
    for (raw, want) in domains {
        let got = ConfigBuilder.normalizeDomain(raw)
        T.check(got == want, "домен «\(raw.replacingOccurrences(of: "\n", with: "\\n"))» → \(want)", "получено «\(got)»")
    }
    for bad in ["", "a b.com", "под домен.рф", "x..y", "[::1]", "a_b.com"] {
        T.check(!ConfigBuilder.isValidDomain(ConfigBuilder.normalizeDomain(bad)), "невалидный домен «\(bad)» отбрасывается")
    }
    T.check(ConfigBuilder.isValidDomain("xn--80ak6aa92e.com"), "punycode-домен валиден")
    let cidrs: [(String, String?)] = [
        ("1.2.3.4", "1.2.3.4/32"), ("10.0.0.0/8", "10.0.0.0/8"), ("2001:db8::1", "2001:db8::1/128"),
        ("2001:db8::/32", "2001:db8::/32"), (" 8.8.8.8 ", "8.8.8.8/32"), ("abc.def", nil), ("1.2.3.4/33", nil),
        ("300.1.1.1", nil), ("1.2.3.4/", nil), ("1.2.3.4/8/9", nil), ("", nil),
    ]
    for (raw, want) in cidrs {
        let got = ConfigBuilder.normalizeCIDR(raw)
        T.check(got == want, "IP «\(raw)» → \(want ?? "отброшен")", "получено \(got ?? "nil")")
    }
    let regexCases: [(String, [String])] = [
        ("/Applications/Google Chrome.app", ["/Google Chrome\\.app/Contents/"]),
        ("/Applications/C++ (beta) [x].app/", ["/C\\+\\+ \\(beta\\) \\[x\\]\\.app/Contents/"]),
        ("/Users/alex/Desktop/Claude.app", ["/Claude\\.app/Contents/"]),
        ("/Applications/Яндекс Музыка.app", ["/Яндекс Музыка\\.app/Contents/"]),
        ("/Applications/$weird^{1}|?.app", ["/\\$weird\\^\\{1\\}\\|\\?\\.app/Contents/"]),
        ("/usr/bin", ["^/usr/bin/"]),
    ]
    for (path, want) in regexCases {
        let got = ConfigBuilder.appPathRegexes(path)
        T.check(got == want, "regex для «\(path)»", "\(got)")
    }
    // The regex must catch the app's helpers and its Cryptex copy, but not similarly named apps.
    func matches(_ rx: String, _ path: String) -> Bool { path.range(of: rx, options: .regularExpression) != nil }
    let claude = ConfigBuilder.appPathRegexes("/Users/alex/Desktop/Claude.app")[0]
    T.check(matches(claude, "/Users/alex/Desktop/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper"), "Claude: хелпер ловится")
    T.check(matches(claude, "/Applications/Claude.app/Contents/MacOS/Claude"), "Claude: после переноса в /Applications правило работает")
    T.check(!matches(claude, "/Applications/Claude Code.app/Contents/MacOS/x") && !matches(claude, "/Applications/NotClaude.app/Contents/MacOS/x"), "Claude: не ловит похожие имена")
    let safariRx = ConfigBuilder.appPathRegexes("/Applications/Safari.app")[0]
    T.check(matches(safariRx, "/System/Volumes/Preboot/Cryptexes/App/System/Applications/Safari.app/Contents/MacOS/Safari"), "Safari: реальный путь в Cryptexes ловится")
    T.check(matches(ConfigBuilder.appPathRegexes("/usr/bin")[0], "/usr/bin/curl") && !matches(ConfigBuilder.appPathRegexes("/usr/bin")[0], "/usr/local/bin/curl"), "папка: только её содержимое")

    T.section("Генератор: семантика правил")
    var p = Profile()
    p.servers = [server("de"), server("ru", port: 8443)]
    p.domainRules = [
        DomainRule(kind: .domain, value: "youtube.com", target: "de"),
        DomainRule(kind: .domain, value: "bad domain.com", target: "de"),
        DomainRule(kind: .ip, value: "not-an-ip", target: "de"),
        DomainRule(kind: .ip, value: "10.8.0.0/16", target: "ru"),
        DomainRule(kind: .keyword, value: "OpenAI", target: "de"),
        DomainRule(kind: .domain, value: "gone.example", target: "deleted-server"),
        DomainRule(kind: .domain, value: "baidu.com", target: Target.direct),
        DomainRule(kind: .ruleset, value: "geosite-youtube, geosite-youtube ,geoip-cn", target: "de"),
        DomainRule(kind: .ruleset, value: "geosite-cn", target: Target.direct),
        DomainRule(kind: .ruleset, value: "not-a-set", target: "de"),
        DomainRule(kind: .domain, value: "   ", target: "de"),
    ]
    var disabled = DomainRule(kind: .domain, value: "disabled.example", target: "de")
    disabled.enabled = false
    p.domainRules.append(disabled)
    var disabledApp = AppRule(name: "Off", path: "/Applications/Off.app", target: "de")
    disabledApp.enabled = false
    p.appRules = [
        AppRule(name: "Telegram", path: "/Applications/Telegram.app", target: "de"),
        AppRule(name: "Safari", path: "/Applications/Safari.app", target: "ru"),
        AppRule(name: "Empty", path: "", target: "de"),
        disabledApp,
    ]
    p.settings.defaultTarget = "ru"
    let c = ConfigBuilder(profile: p).build()
    let rules = routeRules(c)

    T.check(rules.first?["action"] == "sniff", "первое правило — sniff")
    T.check(rules.dropFirst().first?["action"] == "hijack-dns", "второе — перехват DNS")
    T.check(rules.dropFirst(2).first?["ip_is_private"] == .bool(true), "третье — локальная сеть → direct")
    T.check(routeRule(c, key: "domain_suffix", value: "youtube.com")?["outbound"] == "via-de", "youtube.com → via-de")
    T.check(routeRule(c, key: "domain_suffix", value: "bad domain.com") == nil, "невалидный домен пропущен")
    T.check(!rules.contains { $0["ip_cidr"] != nil && $0["ip_cidr"] == .array(["not-an-ip"]) }, "невалидный IP пропущен")
    T.check(routeRule(c, key: "ip_cidr", value: "10.8.0.0/16")?["outbound"] == "via-ru", "подсеть → via-ru")
    T.check(routeRule(c, key: "domain_keyword", value: "openai")?["outbound"] == "via-de", "ключевое слово в нижнем регистре")
    T.check(routeRule(c, key: "domain_suffix", value: "gone.example")?["outbound"] == "direct", "правило на удалённый сервер → direct")
    T.check(routeRule(c, key: "domain_suffix", value: "disabled.example") == nil, "выключенное правило не попадает в конфиг")
    T.check(!rules.contains { $0["process_path_regex"].map { "\($0)".contains("Off") } ?? false }, "выключенное приложение не попадает")
    T.check(!rules.contains { $0["process_path_regex"] == .array(["^/"]) }, "приложение с пустым путём пропущено")

    let rsRule = rules.first { $0["rule_set"] != nil && $0["outbound"] == "via-de" }
    T.check(rsRule?["rule_set"] == .array(["geosite-youtube", "geoip-cn"]), "теги наборов: пробелы, дубли, мусор убраны", "\(rsRule?["rule_set"] ?? .null)")
    if case .array(let sets)? = c["route"]?["rule_set"] {
        let tags = sets.compactMap { $0["tag"]?.stringValue }
        T.check(tags == ["geosite-youtube", "geoip-cn", "geosite-cn"], "rule_set без повторов", "\(tags)")
    } else {
        T.check(false, "rule_set объявлены")
    }

    // Order: all domain rules come before any app rule.
    let lastDomain = rules.lastIndex { $0["domain_suffix"] != nil || $0["rule_set"] != nil || $0["ip_cidr"] != nil || $0["domain_keyword"] != nil } ?? -1
    let firstApp = rules.firstIndex { $0["process_path_regex"] != nil } ?? Int.max
    T.check(lastDomain < firstApp, "все правила доменов стоят раньше правил приложений (вариант А)")
    let tg = rules.first { "\($0["process_path_regex"] ?? .null)".contains("Telegram") }
    T.check(tg?["outbound"] == "via-de", "Telegram → via-de")
    let safari = rules.first { "\($0["process_path_regex"] ?? .null)".contains("Safari") }
    T.check(safari?["outbound"] == "via-ru", "Safari → via-ru")

    T.check(c["route"]?["final"] == "default-route", "final → селектор default-route")
    let outbounds: [JSONValue] = { if case .array(let o)? = c["outbounds"] { return o }; return [] }()
    let dr = outbounds.first { $0["tag"] == "default-route" }
    T.check(dr?["default"] == "via-ru", "default-route по умолчанию указывает на RU")
    T.check(dr?["outbounds"] == .array(["via-de", "via-ru", "direct"]), "default-route содержит все серверы и direct")
    for id in ["de", "ru"] {
        let sel = outbounds.first { $0["tag"] == .string("via-\(id)") }
        T.check(sel?["outbounds"] == .array([.string("srv-\(id)"), "direct"]) && sel?["default"] == .string("srv-\(id)"),
                "селектор via-\(id) = [srv-\(id), direct]")
        T.check(outbounds.contains { $0["tag"] == .string("srv-\(id)") && $0["type"] == "shadowsocks" }, "outbound srv-\(id) на месте")
    }

    // DNS: fake IP for A/AAAA, local for DIRECT domains and LAN names.
    let dns = dnsRules(c)
    T.check(dns.first?["server"] == "dns-local" && "\(dns.first?["domain_suffix"] ?? .null)".contains("local"), "DNS: локальные имена → системный DNS")
    T.check(dns.last?["query_type"] == .array(["A", "AAAA"]) && dns.last?["server"] == "dns-fake", "DNS: A/AAAA → FakeIP последним правилом")
    T.check(dns.contains { $0["domain_suffix"] == .array(["baidu.com"]) && $0["server"] == "dns-local" }, "DNS: DIRECT-домен резолвится по-настоящему")
    T.check(dns.contains { $0["rule_set"] == .array(["geosite-cn"]) && $0["server"] == "dns-local" }, "DNS: DIRECT-набор geosite резолвится по-настоящему")
    T.check(!dns.contains { $0["domain_suffix"] == .array(["youtube.com"]) }, "DNS: домен через сервер идёт в FakeIP")
    T.check(!dns.contains { "\($0["rule_set"] ?? .null)".contains("geoip") }, "DNS: geoip-наборы в DNS-правила не попадают")
    T.check(c["dns"]?["final"] == "dns-local", "DNS final — системный")
    T.check(c["experimental"]?["cache_file"]?["store_fakeip"] == .bool(true), "FakeIP переживает перезапуск")

    let (good, out) = T.singBoxCheck(try! ConfigBuilder(profile: p).data(), name: "semantics")
    T.check(good, "этот профиль проходит sing-box check", out)

    T.section("Генератор: локальные наборы правил")
    let dir = T.work.appendingPathComponent("sets-partial")
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    // Only geosite-youtube "downloaded": rules for missing sets must be dropped, not break the config.
    let real = T.work.appendingPathComponent("rule-sets/geosite-telegram.srs")
    if FileManager.default.fileExists(atPath: real.path) {
        try? FileManager.default.copyItem(at: real, to: dir.appendingPathComponent("geosite-youtube.srs"))
    }
    var b = ConfigBuilder(profile: p)
    b.ruleSetDirectory = dir
    let lc = b.build()
    if case .array(let sets)? = lc["route"]?["rule_set"] {
        T.check(sets.count == 1 && sets[0]["type"] == "local" && sets[0]["tag"] == "geosite-youtube", "объявлен только скачанный набор, type local")
    } else {
        T.check(false, "объявлен только скачанный набор")
    }
    T.check(!routeRules(lc).contains { "\($0["rule_set"] ?? .null)".contains("geosite-cn") }, "правило с нескачанным набором пропущено")
    let (good2, out2) = T.singBoxCheck(try! b.data(), name: "partial-sets")
    T.check(good2, "конфиг с частью наборов проходит check", out2)

    T.section("Генератор: профиль и модель")
    var legacy = #"{"version":1,"servers":[],"domainRules":[],"appRules":[],"settings":{"killSwitch":false,"remoteDNS":"8.8.8.8"},"clashSecret":"s"}"#
    let decoded = try? JSONDecoder().decode(Profile.self, from: Data(legacy.utf8))
    T.check(decoded?.settings.killSwitch == false && decoded?.settings.clashPort == 19190, "старый профиль с лишними и недостающими полями читается")
    legacy = #"{"settings":{}}"#
    T.check((try? JSONDecoder().decode(Profile.self, from: Data(legacy.utf8))) == nil, "профиль без обязательных полей не читается (уйдёт в резервную копию)")
    let roundtrip = try? JSONDecoder().decode(Profile.self, from: JSONEncoder().encode(p))
    T.check(roundtrip == p, "профиль сохраняется и читается без потерь")
    T.check(Profile().clashSecret != Profile().clashSecret, "у каждого профиля свой секрет Clash API")
}

func helperTests() {
    T.section("Вспомогательная логика")
    let codes: [(String, String)] = [("🇩🇪 Frankfurt", "DE"), ("🇷🇺", "RU"), ("Germany-1", "GE"), ("Германия", "ГЕ"), ("", "S"), ("123", "S"), ("x", "X")]
    for (name, want) in codes {
        T.check(Naming.suggestedCode(name) == want, "метка для «\(name)» → \(want)", Naming.suggestedCode(name))
    }
    let bundles: [(String, String?)] = [
        ("/Applications/Telegram.app/Contents/MacOS/Telegram", "/Applications/Telegram.app"),
        ("/Users/alex/Desktop/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper", "/Users/alex/Desktop/Claude.app"),
        ("/System/Volumes/Preboot/Cryptexes/App/System/Applications/Safari.app/Contents/MacOS/Safari", "/System/Volumes/Preboot/Cryptexes/App/System/Applications/Safari.app"),
        ("/usr/bin/curl", nil), ("/Applications/Foo.app", "/Applications/Foo.app"), ("/opt/my.application/bin/x", nil),
    ]
    for (path, want) in bundles {
        T.check(Naming.bundlePath(containing: path) == want, "бандл для \(path)", Naming.bundlePath(containing: path) ?? "nil")
    }
    T.check(Naming.displayName("/Applications/Яндекс Музыка.app") == "Яндекс Музыка", "имя приложения без .app")
    T.check(Naming.usesSharedWebKit("/Applications/Safari.app") && !Naming.usesSharedWebKit("/Applications/Google Chrome.app"), "Safari помечается как WebKit")

    let ids = ["de", "ru"]
    T.check(ConnectionInfo.target(chains: ["srv-de", "via-de"], serverIDs: ids) == "de", "цепочка через сервер → сервер")
    T.check(ConnectionInfo.target(chains: ["srv-ru", "via-ru", "default-route"], serverIDs: ids) == "ru", "через «всё остальное» → сервер")
    T.check(ConnectionInfo.target(chains: ["direct", "via-de"], serverIDs: ids) == "de", "фолбэк kill switch → считается за сервер")
    T.check(ConnectionInfo.target(chains: ["direct", "default-route"], serverIDs: ids) == Target.direct, "напрямую → direct")
    T.check(ConnectionInfo.target(chains: ["srv-gone", "via-gone"], serverIDs: ids) == Target.direct, "удалённый сервер → direct")

    func conns(_ paths: [String?]) -> [ClashAPI.Connection] {
        let items = paths.enumerated().map { i, p in
            #"{"id":"\#(i)","upload":0,"download":0,"chains":["direct"],"metadata":{"network":"tcp"\#(p.map { #","processPath":"\#($0)""# } ?? "")}}"#
        }.joined(separator: ",")
        return (try? JSONDecoder().decode(ClashAPI.Connections.self, from: Data(#"{"downloadTotal":0,"uploadTotal":0,"connections":[\#(items)]}"#.utf8)))?.connections ?? []
    }
    T.check(ConnectionInfo.processLookupBroken(conns(Array(repeating: nil, count: 8))), "8 соединений без процесса → тревога")
    T.check(!ConnectionInfo.processLookupBroken(conns(Array(repeating: nil, count: 4))), "мало соединений → без тревоги")
    T.check(!ConnectionInfo.processLookupBroken(conns(Array(repeating: nil, count: 7) + ["/usr/bin/curl"])), "хоть один процесс найден → без тревоги")
    T.check((try? JSONDecoder().decode(ClashAPI.Connections.self, from: Data(#"{"downloadTotal":1,"uploadTotal":2,"connections":null}"#.utf8))) != nil, "пустой список соединений (null) читается")

    let scutil = """
    Available network connection services in the current set (*=enabled):
    * (Connected)      777EAAF0 VPN (com.example.corp) "Corp VPN"                    [VPN:com.example.corp]
    * (Disconnected)   B7762A34 VPN (com.example.other) "Other VPN"            [VPN:com.example.other]
      (Connected)      AAAA VPN (x.y) "Work VPN"  [VPN:x.y]
    """
    T.check(SystemNetwork.parseConnected(scutil) == ["Corp VPN", "Work VPN"], "разбор scutil: только подключённые", "\(SystemNetwork.parseConnected(scutil))")
    T.check(SystemNetwork.parseConnected("") == [], "пустой вывод scutil")
    print("    сейчас подключены другие VPN: \(SystemNetwork.connectedVPNs())")

    T.section("Профиль на диске")
    let dir = T.work.appendingPathComponent("profile-store")
    ProfileStore.directoryOverride = dir
    defer { ProfileStore.directoryOverride = nil }
    T.check(ProfileStore.load().domainRules.first?.value == "geosite-cn,geoip-cn", "нет файла → стартовый профиль")
    var p = Profile.starter()
    p.servers = [server("de")]
    try? ProfileStore.save(p)
    let attrs = try? FileManager.default.attributesOfItem(atPath: ProfileStore.profileURL.path)
    T.check((attrs?[.posixPermissions] as? Int) == 0o600, "profile.json сохраняется с правами 600 (там ключи)")
    T.check(ProfileStore.load() == p, "сохранение и загрузка")
    try? "{ это не json".write(to: ProfileStore.profileURL, atomically: true, encoding: .utf8)
    let loaded = ProfileStore.load()
    let backups = (try? FileManager.default.contentsOfDirectory(atPath: dir.path))?.filter { $0.hasPrefix("profile.broken-") } ?? []
    T.check(loaded.servers.isEmpty && backups.count == 1, "битый профиль → резервная копия + новый профиль")
    T.check(ProfileStore.loadError?.contains("profile.broken-") == true, "пользователь увидит сообщение о битом профиле")
}

/// The root-side allowlist: everything Nova generates passes, every attack from the audit is refused.
func guardTests() {
    T.section("Защита root: ConfigGuard")
    let setsDir = T.work.appendingPathComponent("guard-sets")
    try? FileManager.default.createDirectory(at: setsDir, withIntermediateDirectories: true)
    let store = RuleSetStore(directory: setsDir)
    store.seed(from: URL(fileURLWithPath: T.singBox).deletingLastPathComponent().appendingPathComponent("rule-sets"))

    var p = Profile.starter()
    p.servers = [server("de"), server("ru", port: 8443)]
    p.domainRules += [DomainRule(kind: .ruleset, value: "geosite-youtube", target: "de"),
                      DomainRule(kind: .domain, value: "x.com", target: "ru")]
    p.appRules = [AppRule(name: "T", path: "/Applications/Telegram.app", target: "de")]
    p.settings.defaultTarget = "de"
    var b = ConfigBuilder(profile: p)
    b.ruleSetDirectory = setsDir
    let good = try! b.data()
    let goodErrors = ConfigGuard.validate(good, ruleSetDirectory: setsDir.path)
    T.check(goodErrors.isEmpty, "конфиг Nova проходит", "\(goodErrors)")
    T.check(ConfigGuard.validate(try! ConfigBuilder(profile: Profile()).data(), ruleSetDirectory: setsDir.path).isEmpty, "пустой профиль проходит")
    var verbose = p
    verbose.settings.verboseLog = true
    T.check(ConfigGuard.validate(try! ConfigBuilder(profile: verbose).data(), ruleSetDirectory: setsDir.path).isEmpty, "подробный лог проходит")

    func mutated(_ change: (inout [String: Any]) -> Void) -> Data {
        var obj = try! JSONSerialization.jsonObject(with: good) as! [String: Any]
        change(&obj)
        return try! JSONSerialization.data(withJSONObject: obj)
    }
    func experimental(_ f: @escaping (inout [String: Any]) -> Void) -> (inout [String: Any]) -> Void {
        { o in var e = o["experimental"] as! [String: Any]; f(&e); o["experimental"] = e }
    }
    func clash(_ f: @escaping (inout [String: Any]) -> Void) -> (inout [String: Any]) -> Void {
        experimental { e in var c = e["clash_api"] as! [String: Any]; f(&c); e["clash_api"] = c }
    }
    func firstOutbound(_ f: @escaping (inout [String: Any]) -> Void) -> (inout [String: Any]) -> Void {
        { o in var obs = o["outbounds"] as! [[String: Any]]; f(&obs[0]); o["outbounds"] = obs }
    }
    let outside = T.work.appendingPathComponent("evil-link.srs")
    try? FileManager.default.removeItem(at: setsDir.appendingPathComponent("evil.srs"))
    try? FileManager.default.createSymbolicLink(atPath: setsDir.appendingPathComponent("evil.srs").path, withDestinationPath: "/etc/master.passwd")
    _ = outside

    let attacks: [(String, (inout [String: Any]) -> Void)] = [
        ("external_ui: / — раздача файлов от root", clash { $0["external_ui"] = "/" }),
        ("external_ui_download_url — распаковка zip от root", clash { $0["external_ui"] = "/tmp/x"; $0["external_ui_download_url"] = "https://e.vil/x.zip" }),
        ("log.output — запись в любой файл", { o in o["log"] = ["level": "info", "output": "/etc/zshrc"] }),
        ("Clash API на 0.0.0.0", clash { $0["external_controller"] = "0.0.0.0:19190" }),
        ("Clash API без секрета", clash { $0["secret"] = "" }),
        ("mixed-инбаунд на 0.0.0.0 — открытый прокси", { o in
            var ib = o["inbounds"] as! [[String: Any]]
            ib.append(["type": "mixed", "listen": "0.0.0.0", "listen_port": 1080]); o["inbounds"] = ib }),
        ("инбаунд заменён на mixed", { o in o["inbounds"] = [["type": "mixed", "listen": "127.0.0.1", "listen_port": 1080]] }),
        ("tun с лишним полем", { o in var ib = o["inbounds"] as! [[String: Any]]; ib[0]["platform"] = ["http_proxy": ["enabled": true]]; o["inbounds"] = ib }),
        ("cache_file.path", experimental { $0["cache_file"] = ["enabled": true, "path": "/etc/x.db"] }),
        ("tls.certificate_path в outbound", firstOutbound { $0["tls"] = ["enabled": true, "certificate_path": "/etc/master.passwd"] }),
        ("ech.config_path глубоко внутри", firstOutbound { $0["tls"] = ["enabled": true, "ech": ["enabled": true, "config_path": "/x"]] }),
        ("набор правил вне папки Nova", { o in
            var r = o["route"] as! [String: Any]; var rs = r["rule_set"] as! [[String: Any]]
            rs[0]["path"] = "/etc/master.passwd"; r["rule_set"] = rs; o["route"] = r }),
        ("набор правил через ../", { o in
            var r = o["route"] as! [String: Any]; var rs = r["rule_set"] as! [[String: Any]]
            rs[0]["path"] = setsDir.path + "/../../../../etc/master.passwd"; r["rule_set"] = rs; o["route"] = r }),
        ("набор правил — симлинк наружу", { o in
            var r = o["route"] as! [String: Any]; var rs = r["rule_set"] as! [[String: Any]]
            rs[0]["path"] = setsDir.appendingPathComponent("evil.srs").path; r["rule_set"] = rs; o["route"] = r }),
        ("remote-набор по http", { o in
            var r = o["route"] as! [String: Any]
            r["rule_set"] = [["type": "remote", "tag": "x", "format": "binary", "url": "http://e.vil/x.srs"]]; o["route"] = r }),
        ("неизвестный раздел верхнего уровня", { o in o["services"] = [["type": "ssm-api"]] }),
        ("DNS-сервер с путём (dhcp/hosts)", { o in
            var d = o["dns"] as! [String: Any]; var s = d["servers"] as! [[String: Any]]
            s.append(["type": "hosts", "tag": "h", "path": ["/etc/hosts"]]); d["servers"] = s; o["dns"] = d }),
        ("outbound неизвестного типа", { o in var obs = o["outbounds"] as! [[String: Any]]; obs.append(["type": "tor", "tag": "t"]); o["outbounds"] = obs }),
    ]
    for (name, change) in attacks {
        let errs = ConfigGuard.validate(mutated(change), ruleSetDirectory: setsDir.path)
        T.check(!errs.isEmpty, "отклонено: \(name)", "прошло без ошибок")
    }
    T.check(!ConfigGuard.validate(Data("not json".utf8), ruleSetDirectory: setsDir.path).isEmpty, "отклонено: не JSON")
}

func securityFlagTests() {
    T.section("Защита: allowInsecure и подробный лог")
    func srv(_ link: String) -> Server? {
        (try? LinkParser.parse(link)).map { Server(name: $0.name, code: "S", colorHex: "#fff", outbound: $0.outbound) }
    }
    T.check(srv("trojan://p@t.example:443?allowInsecure=1")?.skipsCertificateCheck == true, "trojan с allowInsecure=1 помечен")
    T.check(srv("hy2://p@h.example:443?insecure=1")?.skipsCertificateCheck == true, "hy2 с insecure=1 помечен")
    T.check(srv("trojan://p@t.example:443?sni=t.example")?.skipsCertificateCheck == false, "обычный TLS не помечен")
    T.check(srv("vless://11111111-2222-3333-4444-555555555555@h:443?security=reality&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0")?.skipsCertificateCheck == false,
            "Reality не помечен")
    var p = Profile()
    T.check(ConfigBuilder(profile: p).build()["log"]?["level"] == "warn", "по умолчанию лог без доменов (warn)")
    p.settings.verboseLog = true
    T.check(ConfigBuilder(profile: p).build()["log"]?["level"] == "info", "подробный лог — info")
    let old = try? JSONDecoder().decode(Profile.self, from: Data(#"{"version":1,"servers":[],"domainRules":[],"appRules":[],"settings":{},"clashSecret":"0123456789abcdef0123"}"#.utf8))
    T.check(old?.settings.verboseLog == false, "старый профиль: подробный лог выключен")
}
