import Foundation

/// Route target: `Target.direct` or a server id.
public typealias TargetID = String

public enum Target {
    public static let direct: TargetID = "direct"
}

public struct Server: Codable, Identifiable, Hashable {
    public var id: String
    public var name: String
    /// Short label shown on chips, e.g. "DE".
    public var code: String
    public var colorHex: String
    /// sing-box outbound object without `tag`.
    public var outbound: JSONValue

    public init(id: String = Server.makeID(), name: String, code: String, colorHex: String, outbound: JSONValue) {
        self.id = id
        self.name = name
        self.code = code
        self.colorHex = colorHex
        self.outbound = outbound
    }

    public static func makeID() -> String {
        String(UUID().uuidString.lowercased().prefix(8))
    }

    public var protocolName: String { outbound["type"]?.stringValue ?? "?" }
    public var host: String { outbound["server"]?.stringValue ?? "" }
    public var port: Int? { outbound["server_port"]?.intValue }

    /// TLS certificate checking is turned off (`allowInsecure` / `insecure` in the link) and there's no
    /// REALITY to authenticate the server instead: traffic can be intercepted.
    public var skipsCertificateCheck: Bool {
        outbound["tls"]?["insecure"] == .bool(true) && outbound["tls"]?["reality"]?["enabled"] != .bool(true)
    }

    public static let palette = ["#8B7CFF", "#FF7A59", "#3BE8B0", "#4DA3FF", "#FFC14D", "#FF5C9A", "#A3E635"]
}

public enum DomainRuleKind: String, Codable, CaseIterable, Identifiable {
    /// Domain and all its subdomains (`youtube.com`), or a zone (`.ru`).
    case domain
    case keyword
    case ip
    /// One or more remote rule sets, comma-separated tags.
    case ruleset

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .domain: return "Домен"
        case .keyword: return "Ключевое слово"
        case .ip: return "IP / подсеть"
        case .ruleset: return "Набор"
        }
    }
}

public struct DomainRule: Codable, Identifiable, Hashable {
    public var id: String = UUID().uuidString
    public var kind: DomainRuleKind
    public var value: String
    public var target: TargetID
    public var enabled: Bool = true

    public init(kind: DomainRuleKind, value: String, target: TargetID) {
        self.kind = kind
        self.value = value
        self.target = target
    }
}

public struct AppRule: Codable, Identifiable, Hashable {
    public var id: String = UUID().uuidString
    public var name: String
    /// Bundle path, e.g. `/Applications/Telegram.app`.
    public var path: String
    public var target: TargetID
    public var enabled: Bool = true

    public init(name: String, path: String, target: TargetID) {
        self.name = name
        self.path = path
        self.target = target
    }
}

public struct Settings: Codable, Hashable {
    public var defaultTarget: TargetID = Target.direct
    public var killSwitch: Bool = true
    public var launchAtLogin: Bool = false
    public var connectOnLaunch: Bool = false
    public var showSpeedInMenuBar: Bool = false
    /// Log every connection (domains, apps). Off by default: the log then holds only warnings and errors.
    public var verboseLog: Bool = false
    public var autoUpdateRuleSets: Bool = true
    public var clashPort: Int = 19190

    public init() {}

    public init(from decoder: Decoder) throws {
        // Tolerate missing keys so older profiles keep loading.
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Settings()
        defaultTarget = try c.decodeIfPresent(TargetID.self, forKey: .defaultTarget) ?? d.defaultTarget
        killSwitch = try c.decodeIfPresent(Bool.self, forKey: .killSwitch) ?? d.killSwitch
        launchAtLogin = try c.decodeIfPresent(Bool.self, forKey: .launchAtLogin) ?? d.launchAtLogin
        connectOnLaunch = try c.decodeIfPresent(Bool.self, forKey: .connectOnLaunch) ?? d.connectOnLaunch
        showSpeedInMenuBar = try c.decodeIfPresent(Bool.self, forKey: .showSpeedInMenuBar) ?? d.showSpeedInMenuBar
        verboseLog = try c.decodeIfPresent(Bool.self, forKey: .verboseLog) ?? d.verboseLog
        autoUpdateRuleSets = try c.decodeIfPresent(Bool.self, forKey: .autoUpdateRuleSets) ?? d.autoUpdateRuleSets
        clashPort = try c.decodeIfPresent(Int.self, forKey: .clashPort) ?? d.clashPort
    }
}

public struct Profile: Codable, Hashable {
    public var version: Int = 1
    public var servers: [Server] = []
    public var domainRules: [DomainRule] = []
    public var appRules: [AppRule] = []
    public var settings = Settings()
    public var clashSecret: String = UUID().uuidString

    public init() {}

    public func server(_ id: TargetID) -> Server? { servers.first { $0.id == id } }

    /// Starter rules: local-only presets that don't depend on servers.
    public static func starter() -> Profile {
        var p = Profile()
        p.domainRules = [DomainRule(kind: .ruleset, value: "geosite-cn,geoip-cn", target: Target.direct)]
        return p
    }
}

/// Built-in rule sets from SagerNet/sing-geosite and sing-geoip.
public struct RuleSetPreset: Identifiable, Hashable {
    public var id: String { tags.joined(separator: ",") }
    public let title: String
    public let tags: [String]

    public static let all: [RuleSetPreset] = [
        .init(title: "YouTube", tags: ["geosite-youtube"]),
        .init(title: "Google", tags: ["geosite-google"]),
        .init(title: "Telegram", tags: ["geosite-telegram"]),
        .init(title: "OpenAI / ChatGPT", tags: ["geosite-openai"]),
        .init(title: "Anthropic / Claude", tags: ["geosite-anthropic"]),
        .init(title: "Instagram", tags: ["geosite-instagram"]),
        .init(title: "Facebook", tags: ["geosite-facebook"]),
        .init(title: "Twitter / X", tags: ["geosite-twitter"]),
        .init(title: "Discord", tags: ["geosite-discord"]),
        .init(title: "Netflix", tags: ["geosite-netflix"]),
        .init(title: "Spotify", tags: ["geosite-spotify"]),
        .init(title: "GitHub", tags: ["geosite-github"]),
        .init(title: "Китай (сайты и IP)", tags: ["geosite-cn", "geoip-cn"]),
        .init(title: "Россия (IP)", tags: ["geoip-ru"]),
    ]

    public static func title(for value: String) -> String {
        all.first { $0.id == value }?.title ?? value
    }
}
