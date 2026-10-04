import Foundation

/// Turns a user `Profile` into a sing-box 1.14 config.
///
/// Route order (decision "вариант А"):
/// sniff → DNS hijack → private IPs → domain rules → app rules → default target.
public struct ConfigBuilder {
    public enum Inbound {
        case tun
        /// Local HTTP/SOCKS proxy, used by the self-test (no root needed).
        case mixed(port: Int)
    }

    public static let directTag = "direct"
    /// Selector for "всё остальное", switched live from the menu.
    public static let defaultRouteTag = "default-route"
    public static let localDNSTag = "dns-local"

    public static func serverTag(_ id: String) -> String { "srv-\(id)" }
    public static func selectorTag(_ id: String) -> String { "via-\(id)" }
    static let fakeDNSTag = "dns-fake"

    public let profile: Profile
    public var inbound: Inbound = .tun
    /// nil → from the profile: "info" with verbose logging, otherwise "warn" (no per-connection lines).
    public var logLevel: String?
    /// When set, rule sets are read from `<dir>/<tag>.srs` (see `RuleSetStore`) instead of downloaded by the core.
    public var ruleSetDirectory: URL?

    public init(profile: Profile, inbound: Inbound = .tun) {
        self.profile = profile
        self.inbound = inbound
    }

    /// Outbound tag for a target; unknown servers fall back to direct.
    public func outboundTag(_ target: TargetID) -> String {
        if target != Target.direct, profile.server(target) != nil { return Self.selectorTag(target) }
        return Self.directTag
    }

    public func build() -> JSONValue {
        var ruleSetTags: [String] = []
        func useRuleSets(_ tags: [String]) {
            for t in tags where !ruleSetTags.contains(t) { ruleSetTags.append(t) }
        }

        // Outbounds: each server is wrapped in a selector [server, direct] so the
        // kill switch monitor can fall back to direct without a restart.
        var outbounds: [JSONValue] = []
        for s in profile.servers {
            var ob = s.outbound
            ob["tag"] = .string(Self.serverTag(s.id))
            outbounds.append(ob)
            outbounds.append([
                "type": "selector",
                "tag": .string(Self.selectorTag(s.id)),
                "outbounds": [.string(Self.serverTag(s.id)), .string(Self.directTag)],
                "default": .string(Self.serverTag(s.id)),
                "interrupt_exist_connections": true,
            ])
        }
        outbounds.append(["type": "direct", "tag": .string(Self.directTag)])

        // Route + DNS rules.
        var routeRules: [JSONValue] = [
            ["action": "sniff"],
            ["protocol": "dns", "action": "hijack-dns"],
            ["ip_is_private": true, "outbound": .string(Self.directTag)],
        ]
        // FakeIP range 198.19.0.0/16: other clients (Clash-based apps and the like) commonly take 198.18.0.0/16.
        // DNS: A/AAAA get fake IPs, so proxied connections carry the domain and the
        // server resolves it (no ISP DNS poisoning, no dependency on one server's DoH).
        // Local names and domains routed DIRECT keep real answers from the system resolver.
        var dnsRules: [JSONValue] = [
            ["domain_suffix": ["local", "lan", "localhost", "home.arpa", "internal"], "server": .string(Self.localDNSTag)],
        ]

        for r in profile.domainRules where r.enabled {
            guard let match = matcher(for: r) else { continue }
            var rule = match
            rule["outbound"] = .string(outboundTag(r.target))
            routeRules.append(rule)

            if case .ruleset = r.kind { useRuleSets(availableRuleSets(r.value)) }
            guard r.target == Target.direct || profile.server(r.target) == nil else { continue }
            switch r.kind {
            case .ruleset:
                // DNS rules may only reference domain rule sets.
                let domainSets = availableRuleSets(r.value).filter { $0.hasPrefix("geosite-") }
                if !domainSets.isEmpty {
                    dnsRules.append(["rule_set": .array(domainSets.map { .string($0) }), "server": .string(Self.localDNSTag)])
                }
            case .domain, .keyword:
                var d = match
                d["server"] = .string(Self.localDNSTag)
                dnsRules.append(d)
            case .ip:
                break
            }
        }

        for a in profile.appRules where a.enabled && !a.path.isEmpty {
            routeRules.append([
                "process_path_regex": .array(Self.appPathRegexes(a.path).map { .string($0) }),
                "outbound": .string(outboundTag(a.target)),
            ])
        }

        let defaultTarget = profile.settings.defaultTarget
        let finalOutbound = outboundTag(defaultTarget)
        outbounds.append([
            "type": "selector",
            "tag": .string(Self.defaultRouteTag),
            "outbounds": .array(profile.servers.map { .string(Self.selectorTag($0.id)) } + [.string(Self.directTag)]),
            "default": .string(finalOutbound),
            "interrupt_exist_connections": true,
        ])

        dnsRules.append(["query_type": ["A", "AAAA"], "server": .string(Self.fakeDNSTag)])
        let dnsServers: [JSONValue] = [
            ["type": "local", "tag": .string(Self.localDNSTag)],
            ["type": "fakeip", "tag": .string(Self.fakeDNSTag), "inet4_range": "198.19.0.0/16", "inet6_range": "fc00::/18"],
        ]

        let ruleSets: [JSONValue] = ruleSetTags.map { tag in
            if let dir = ruleSetDirectory {
                return [
                    "type": "local",
                    "tag": .string(tag),
                    "format": "binary",
                    "path": .string(dir.appendingPathComponent("\(tag).srs").path),
                ]
            }
            return [
                "type": "remote",
                "tag": .string(tag),
                "format": "binary",
                "url": .string(Self.ruleSetURL(tag)),
                "download_detour": .string(Self.defaultRouteTag),
                "update_interval": profile.settings.autoUpdateRuleSets ? "7d" : "365d",
            ]
        }

        var route: [String: JSONValue] = [
            "rules": .array(routeRules),
            "final": .string(Self.defaultRouteTag),
            "auto_detect_interface": true,
            "find_process": true,
            "default_domain_resolver": .string(Self.localDNSTag),
        ]
        if !ruleSets.isEmpty { route["rule_set"] = .array(ruleSets) }

        let dns: [String: JSONValue] = [
            "servers": .array(dnsServers),
            "rules": .array(dnsRules),
            "final": .string(Self.localDNSTag),
            "strategy": "prefer_ipv4",
        ]

        let inboundJSON: JSONValue
        switch inbound {
        case .tun:
            inboundJSON = [
                "type": "tun",
                "tag": "tun-in",
                "address": ["172.19.0.1/30", "fdfe:dcba:9876::1/126"],
                "auto_route": true,
                "strict_route": true,
                "stack": "mixed",
            ]
        case .mixed(let port):
            inboundJSON = ["type": "mixed", "tag": "mixed-in", "listen": "127.0.0.1", "listen_port": .int(port)]
        }

        return [
            "log": ["level": .string(logLevel ?? (profile.settings.verboseLog ? "info" : "warn")), "timestamp": true],
            "dns": .object(dns),
            "inbounds": [inboundJSON],
            "outbounds": .array(outbounds),
            "route": .object(route),
            "experimental": [
                "clash_api": [
                    "external_controller": .string("127.0.0.1:\(profile.settings.clashPort)"),
                    "secret": .string(profile.clashSecret),
                ],
                "cache_file": ["enabled": true, "store_fakeip": true],
            ],
        ]
    }

    public func data() throws -> Data {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return try enc.encode(build())
    }

    // MARK: - Matchers

    /// sing-box matcher for a rule, or nil if the rule is empty or invalid (then it's skipped,
    /// so one bad rule can't stop the core from starting).
    func matcher(for r: DomainRule) -> JSONValue? {
        let v = r.value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !v.isEmpty else { return nil }
        switch r.kind {
        case .domain:
            let d = Self.normalizeDomain(v)
            return Self.isValidDomain(d) ? ["domain_suffix": [.string(d)]] : nil
        case .keyword:
            return ["domain_keyword": [.string(v.lowercased())]]
        case .ip:
            return Self.normalizeCIDR(v).map { ["ip_cidr": [.string($0)]] }
        case .ruleset:
            let tags = availableRuleSets(v)
            return tags.isEmpty ? nil : ["rule_set": .array(tags.map { .string($0) })]
        }
    }

    /// Tags of a rule that can actually be loaded (files present when reading from disk).
    func availableRuleSets(_ value: String) -> [String] {
        var tags: [String] = []
        for t in Self.ruleSetTags(value) where Self.isValidRuleSetTag(t) && !tags.contains(t) { tags.append(t) }
        guard let dir = ruleSetDirectory else { return tags }
        return tags.filter { FileManager.default.fileExists(atPath: dir.appendingPathComponent("\($0).srs").path) }
    }

    /// `*.youtube.com`, `.ru`, `https://YouTube.com:443/x`, `example.com.` → `youtube.com`, `ru`, `example.com`.
    public static func normalizeDomain(_ raw: String) -> String {
        var s = raw.lowercased().trimmingCharacters(in: .whitespacesAndNewlines)
        if let r = s.range(of: "://") { s = String(s[r.upperBound...]) }
        if let cut = s.firstIndex(where: { "/?#".contains($0) }) { s = String(s[..<cut]) }
        if let at = s.lastIndex(of: "@") { s = String(s[s.index(after: at)...]) }
        if let colon = s.lastIndex(of: ":"), Int(s[s.index(after: colon)...]) != nil { s = String(s[..<colon]) }
        while s.hasPrefix("*.") { s.removeFirst(2) }
        while s.hasPrefix(".") { s.removeFirst() }
        while s.hasSuffix(".") { s.removeLast() }
        return s
    }

    public static func isValidDomain(_ d: String) -> Bool {
        guard !d.isEmpty, d.count <= 253 else { return false }
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-."))
        return d.unicodeScalars.allSatisfy { allowed.contains($0) } && !d.contains("..")
    }

    /// `1.2.3.4` → `1.2.3.4/32`, `2001:db8::1` → `/128`; nil for anything that isn't an IP or subnet.
    public static func normalizeCIDR(_ raw: String) -> String? {
        let s = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let parts = s.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard parts.count <= 2, let ip = parts.first, !ip.isEmpty else { return nil }
        var buf = [UInt8](repeating: 0, count: 16)
        let isV4 = inet_pton(AF_INET, ip, &buf) == 1
        let isV6 = !isV4 && inet_pton(AF_INET6, ip, &buf) == 1
        guard isV4 || isV6 else { return nil }
        let maxBits = isV4 ? 32 : 128
        if parts.count == 2 {
            guard let bits = Int(parts[1]), (0...maxBits).contains(bits) else { return nil }
            return "\(ip)/\(bits)"
        }
        return "\(ip)/\(maxBits)"
    }

    static func isValidRuleSetTag(_ tag: String) -> Bool {
        let allowed = CharacterSet.lowercaseLetters.union(.decimalDigits).union(CharacterSet(charactersIn: "-_!@"))
        return (tag.hasPrefix("geosite-") || tag.hasPrefix("geoip-")) && tag.unicodeScalars.allSatisfy { allowed.contains($0) }
    }

    public static func ruleSetTags(_ value: String) -> [String] {
        value.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    static func ruleSetURL(_ tag: String) -> String {
        let repo = tag.hasPrefix("geoip-") ? "sing-geoip" : "sing-geosite"
        return "https://raw.githubusercontent.com/SagerNet/\(repo)/rule-set/\(tag).srs"
    }

    /// Regex for an app rule: the bundle by name, wherever it lives (`/Applications`, the Desktop,
    /// a Cryptex for Safari), helpers inside it included. Moving or updating the app keeps the rule working.
    /// A non-bundle path (e.g. `/usr/bin`) matches everything under that folder.
    public static func appPathRegexes(_ bundlePath: String) -> [String] {
        var p = bundlePath
        while p.hasSuffix("/") { p.removeLast() }
        let name = (p as NSString).lastPathComponent
        guard name.hasSuffix(".app") else { return [appPathRegex(p)] }
        return ["/\(escapeRegex(name))/Contents/"]
    }

    /// Matches every process under `path` (absolute).
    public static func appPathRegex(_ path: String) -> String {
        var p = path
        while p.hasSuffix("/") { p.removeLast() }
        return "^\(escapeRegex(p))/"
    }

    static func escapeRegex(_ s: String) -> String {
        let special = Set("\\.+*?()|[]{}^$")
        return s.map { special.contains($0) ? "\\\($0)" : String($0) }.joined()
    }

}
