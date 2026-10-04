import Foundation

/// Loads and saves `profile.json` in ~/Library/Application Support/Nova.
public enum ProfileStore {
    /// Tests point this at a temp folder.
    public static var directoryOverride: URL?

    public static var directory: URL {
        directoryOverride ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Nova", isDirectory: true)
    }

    public static var profileURL: URL { directory.appendingPathComponent("profile.json") }

    public static func load() -> Profile {
        guard let data = try? Data(contentsOf: profileURL) else { return Profile.starter() }
        do {
            return try JSONDecoder().decode(Profile.self, from: data)
        } catch {
            // Keep the unreadable file instead of silently overwriting it on the next save.
            let backup = directory.appendingPathComponent("profile.broken-\(Int(Date().timeIntervalSince1970)).json")
            try? FileManager.default.copyItem(at: profileURL, to: backup)
            loadError = "profile.json не читается, сохранён как \(backup.lastPathComponent). Начат новый профиль."
            return Profile.starter()
        }
    }

    /// Set when the last `load()` hit an unreadable profile.
    public private(set) static var loadError: String?

    public static func save(_ profile: Profile) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        try enc.encode(profile).write(to: profileURL, options: .atomic)
        // Holds server keys.
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: profileURL.path)
    }
}
