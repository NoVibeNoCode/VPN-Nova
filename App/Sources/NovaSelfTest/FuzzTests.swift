import Foundation
import NovaCore

/// Random profiles (seeded, reproducible) must always produce a config sing-box accepts.
func fuzzTests(count: Int) {
    T.section("Фаззинг: \(count) случайных профилей → sing-box check")
    var rng = SeededRNG(seed: 20261003)
    let protocols: [JSONValue] = [
        ["type": "shadowsocks", "server": "1.2.3.4", "server_port": 8388, "method": "aes-128-gcm", "password": "p"],
        ["type": "vless", "server": "h.example", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555", "flow": "xtls-rprx-vision",
         "tls": ["enabled": true, "server_name": "a.com", "utls": ["enabled": true, "fingerprint": "chrome"],
                 "reality": ["enabled": true, "public_key": "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", "short_id": ""]]],
        ["type": "trojan", "server": "t.example", "server_port": 443, "password": "p", "tls": ["enabled": true]],
        ["type": "vmess", "server": "v.example", "server_port": 10086, "uuid": "11111111-2222-3333-4444-555555555555", "security": "auto"],
        ["type": "hysteria2", "server": "h2.example", "server_port": 443, "password": "p", "tls": ["enabled": true]],
        ["type": "tuic", "server": "tu.example", "server_port": 443, "uuid": "11111111-2222-3333-4444-555555555555", "password": "p",
         "tls": ["enabled": true, "alpn": ["h3"]]],
    ]
    let values: [DomainRuleKind: [String]] = [
        .domain: ["youtube.com", ".ru", "*.cn", "https://A.B.c/x", "x.y:8080", "bad host", "", "例子.cn", "xn--p1ai", "a.b."],
        .keyword: ["google", "OpenAI", "", "a b", "яндекс", "\"quote"],
        .ip: ["1.1.1.1", "10.0.0.0/8", "2001:db8::/32", "999.1.1.1", "x", "fe80::1%en0", "1.2.3.4/40"],
        .ruleset: ["geosite-youtube", "geosite-cn,geoip-cn", "geoip-ru", "junk", "geosite-x y", ",,", "GEOSITE-CN"],
    ]
    let paths = ["/Applications/Telegram.app", "/Applications/Google Chrome.app", "/Applications/C++ [x](y).app",
                 "/Applications/Яндекс.app", "/Applications/Safari.app", "", "/usr/bin", "/Applications/a\\b.app"]

    var failed = 0
    for i in 0..<count {
        var p = Profile()
        let nServers = Int.random(in: 0...5, using: &rng)
        p.servers = (0..<nServers).map { j in
            Server(id: "s\(j)", name: "S\(j)", code: "S\(j)", colorHex: "#fff", outbound: protocols.randomElement(using: &rng)!)
        }
        let targets = p.servers.map(\.id) + [Target.direct, "ghost"]
        for _ in 0..<Int.random(in: 0...25, using: &rng) {
            let kind = DomainRuleKind.allCases.randomElement(using: &rng)!
            var r = DomainRule(kind: kind, value: values[kind]!.randomElement(using: &rng)!, target: targets.randomElement(using: &rng)!)
            r.enabled = Int.random(in: 0...5, using: &rng) > 0
            p.domainRules.append(r)
        }
        for _ in 0..<Int.random(in: 0...8, using: &rng) {
            var a = AppRule(name: "a", path: paths.randomElement(using: &rng)!, target: targets.randomElement(using: &rng)!)
            a.enabled = Int.random(in: 0...5, using: &rng) > 0
            p.appRules.append(a)
        }
        p.settings.defaultTarget = targets.randomElement(using: &rng)!
        p.settings.autoUpdateRuleSets = Bool.random(using: &rng)
        let data = try! ConfigBuilder(profile: p).data()
        let guardErrors = ConfigGuard.validate(data, ruleSetDirectory: "/nonexistent")
        if !guardErrors.isEmpty {
            failed += 1
            if failed <= 3 { print("    профиль #\(i) отклонён ConfigGuard: \(guardErrors.prefix(3))") }
            continue
        }
        let (good, out) = T.singBoxCheck(data, name: "fuzz")
        if !good {
            failed += 1
            if failed <= 3 {
                let dump = T.work.appendingPathComponent("fuzz-fail-\(i).json")
                try? JSONEncoder().encode(p).write(to: dump)
                print("    профиль #\(i) не прошёл: \(out.prefix(300)) → \(dump.path)")
            }
        }
    }
    T.check(failed == 0, "все \(count) профилей проходят ConfigGuard и sing-box check", "не прошли: \(failed)")
}

/// SplitMix64 — deterministic, so a failing fuzz case can be reproduced.
struct SeededRNG: RandomNumberGenerator {
    var state: UInt64
    init(seed: UInt64) { state = seed }
    mutating func next() -> UInt64 {
        state &+= 0x9E3779B97F4A7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58476D1CE4E5B9
        z = (z ^ (z >> 27)) &* 0x94D049BB133111EB
        return z ^ (z >> 31)
    }
}
