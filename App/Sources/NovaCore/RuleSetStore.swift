import Foundation

/// Downloads rule sets (`geosite-*.srs`, `geoip-*.srs`) into a local folder so the core
/// starts without network access and never depends on a server being up.
public struct RuleSetStore {
    public let directory: URL

    public init(directory: URL = ProfileStore.directory.appendingPathComponent("rule-sets", isDirectory: true)) {
        self.directory = directory
    }

    public func path(for tag: String) -> URL { directory.appendingPathComponent("\(tag).srs") }

    public static let maxAge: TimeInterval = 7 * 24 * 3600

    /// Copies rule sets shipped inside the app into the store when they're missing,
    /// so connecting never waits for GitHub.
    public func seed(from bundled: URL?) {
        guard let bundled, let files = try? FileManager.default.contentsOfDirectory(atPath: bundled.path) else { return }
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for f in files where f.hasSuffix(".srs") {
            let dst = directory.appendingPathComponent(f)
            if !FileManager.default.fileExists(atPath: dst.path) {
                try? FileManager.default.copyItem(at: bundled.appendingPathComponent(f), to: dst)
            }
        }
    }

    /// Tags with no file yet.
    public func missing(_ tags: [String]) -> [String] {
        tags.filter { !FileManager.default.fileExists(atPath: path(for: $0).path) }
    }

    /// Tags whose file is older than `maxAge`.
    public func stale(_ tags: [String]) -> [String] {
        tags.filter {
            guard let d = (try? FileManager.default.attributesOfItem(atPath: path(for: $0).path))?[.modificationDate] as? Date else { return false }
            return Date().timeIntervalSince(d) > Self.maxAge
        }
    }

    public enum StoreError: LocalizedError {
        case download(String, String)
        public var errorDescription: String? {
            switch self {
            case .download(let tag, let why): return "Не удалось скачать набор \(tag): \(why)"
            }
        }
    }

    /// Makes sure every tag has a file. Stale files are refreshed when `refresh` is set;
    /// a failed refresh keeps the old file. Returns tags that have no file and couldn't be fetched
    /// (their rules are skipped by the config builder).
    @discardableResult
    public func ensure(_ tags: [String], refresh: Bool) async -> [String: String] {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return await withTaskGroup(of: (String, String?).self) { group in
            for tag in tags {
                let file = path(for: tag)
                let attrs = try? FileManager.default.attributesOfItem(atPath: file.path)
                let age = (attrs?[.modificationDate] as? Date).map { Date().timeIntervalSince($0) }
                if let age, !(refresh && age > Self.maxAge) { continue }
                let exists = age != nil
                group.addTask {
                    do {
                        try await download(tag, to: file)
                        return (tag, nil)
                    } catch {
                        return (tag, exists ? nil : error.localizedDescription)
                    }
                }
            }
            var failed: [String: String] = [:]
            for await (tag, err) in group { if let err { failed[tag] = err } }
            return failed
        }
    }

    func download(_ tag: String, to file: URL) async throws {
        guard let url = URL(string: ConfigBuilder.ruleSetURL(tag)) else { return }
        var req = URLRequest(url: url)
        req.timeoutInterval = 15
        let (tmp, resp) = try await URLSession.shared.download(for: req)
        let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
        guard code == 200 else {
            throw StoreError.download(tag, code == 404 ? "такого набора нет" : "HTTP \(code)")
        }
        _ = try? FileManager.default.removeItem(at: file)
        try FileManager.default.moveItem(at: tmp, to: file)
    }

    /// All rule-set tags a profile uses.
    public static func tags(in profile: Profile) -> [String] {
        var out: [String] = []
        for r in profile.domainRules where r.enabled && r.kind == .ruleset {
            for t in ConfigBuilder.ruleSetTags(r.value) where !out.contains(t) { out.append(t) }
        }
        return out
    }
}
