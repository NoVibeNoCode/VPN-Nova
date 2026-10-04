import Foundation

/// Small pure helpers used by the UI, kept here so the self-test covers them.
public enum Naming {
    /// "🇩🇪 Frankfurt" → "DE", "Germany-1" → "GE", "" → "S".
    public static func suggestedCode(_ name: String) -> String {
        let flags = name.unicodeScalars.filter { (0x1F1E6...0x1F1FF).contains($0.value) }
        if flags.count >= 2 {
            return String(flags.prefix(2).compactMap { UnicodeScalar($0.value - 0x1F1E6 + 65).map(Character.init) })
        }
        let letters = String(name.filter(\.isLetter).prefix(2)).uppercased()
        return letters.isEmpty ? "S" : letters
    }

    /// `/Applications/Foo.app/Contents/Frameworks/Foo Helper.app/…` → `/Applications/Foo.app` (outermost bundle).
    public static func bundlePath(containing path: String) -> String? {
        guard let r = path.range(of: ".app/") ?? (path.hasSuffix(".app") ? path.range(of: ".app", options: .backwards) : nil) else { return nil }
        return String(path[..<r.lowerBound]) + ".app"
    }

    public static func displayName(_ path: String) -> String {
        let name = (path as NSString).lastPathComponent
        return name.hasSuffix(".app") ? String(name.dropLast(4)) : name
    }

    /// Safari's traffic goes through com.apple.WebKit.Networking, outside its bundle,
    /// so an app rule can't catch it.
    public static func usesSharedWebKit(_ path: String) -> Bool {
        path.hasSuffix("/Safari.app") || path.hasSuffix("/Safari Technology Preview.app")
    }
}

public enum ConnectionInfo {
    /// The server a live connection actually went through (by its outbound chain), or direct.
    public static func target(chains: [String], serverIDs: [String]) -> TargetID {
        for id in serverIDs where chains.contains(ConfigBuilder.selectorTag(id)) || chains.contains(ConfigBuilder.serverTag(id)) {
            return id
        }
        return Target.direct
    }

    /// True when the core clearly can't attribute connections to processes (app rules would never match).
    public static func processLookupBroken(_ connections: [ClashAPI.Connection]) -> Bool {
        let tcp = connections.filter { $0.metadata.network == "tcp" }
        return tcp.count >= 6 && tcp.allSatisfy { ($0.metadata.processPath ?? "").isEmpty }
    }
}
