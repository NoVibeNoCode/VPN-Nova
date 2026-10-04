import Foundation

/// Fixed system locations used by the installer, `novactl` and the app.
public enum SystemPaths {
    public static let label = "com.nova.vpn.core"
    public static let dataDir = "/Library/Application Support/Nova"
    public static let novactl = "/Library/PrivilegedHelperTools/com.nova.vpn.novactl"
    public static let novaguard = "/Library/PrivilegedHelperTools/com.nova.vpn.novaguard"
    public static let sudoers = "/etc/sudoers.d/nova-vpn"
    public static let logFile = "/Library/Logs/Nova/sing-box.log"
    public static let versionFile = "\(dataDir)/installed-version"
}

public enum CoreError: LocalizedError {
    case command(String)

    public var errorDescription: String? {
        switch self { case .command(let m): return m }
    }
}

/// Starts/stops the sing-box core.
public protocol CoreService {
    func apply(config: Data) throws
    func start() throws
    func stop() throws
    func isRunning() -> Bool
}

@discardableResult
func run(_ exe: String, _ args: [String], stdin: Data? = nil) throws -> String {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: exe)
    p.arguments = args
    let out = Pipe()
    p.standardOutput = out
    p.standardError = out
    if stdin != nil {
        p.standardInput = Pipe()
        // A child that exits early (sudo refusing) closes its stdin; don't let the write kill us.
        signal(SIGPIPE, SIG_IGN)
    }
    try p.run()
    if let stdin, let pipe = p.standardInput as? Pipe {
        try? pipe.fileHandleForWriting.write(contentsOf: stdin)
        try? pipe.fileHandleForWriting.close()
    }
    let data = out.fileHandleForReading.readDataToEndOfFile()
    p.waitUntilExit()
    let text = String(data: data, encoding: .utf8) ?? ""
    guard p.terminationStatus == 0 else {
        throw CoreError.command(text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            ? "\(exe) завершился с кодом \(p.terminationStatus)" : text)
    }
    return text
}

/// Production core: LaunchDaemon controlled through `sudo -n novactl` (rule in /etc/sudoers.d).
public final class PrivilegedCore: CoreService {
    let stagingDir: URL

    public init(stagingDir: URL) {
        self.stagingDir = stagingDir
    }

    public static var isInstalled: Bool {
        FileManager.default.fileExists(atPath: SystemPaths.novactl)
            && FileManager.default.fileExists(atPath: SystemPaths.sudoers)
    }

    public static var installedVersion: String? {
        (try? String(contentsOfFile: SystemPaths.versionFile, encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    func ctl(_ args: String...) throws {
        try run("/usr/bin/sudo", ["-n", SystemPaths.novactl] + args)
    }

    /// The config goes to root over stdin: root never opens a path chosen by the user.
    public func apply(config: Data) throws {
        // Older versions staged the config here; it holds server keys.
        try? FileManager.default.removeItem(at: stagingDir.appendingPathComponent("config.staged.json"))
        try run("/usr/bin/sudo", ["-n", SystemPaths.novactl, "apply"], stdin: config)
    }

    public func start() throws { try ctl("start") }
    public func stop() throws { try ctl("stop") }

    public func isRunning() -> Bool {
        (try? run("/usr/bin/sudo", ["-n", SystemPaths.novactl, "status"]))?.contains("running") == true
    }

    /// One-time install with the system admin password prompt.
    public static func install(resources: URL, version: String) throws {
        let script = resources.appendingPathComponent("install.sh").path
        let cmd = [script, NSUserName(), resources.path, version].map(shellQuote).joined(separator: " ")
        try runAsAdmin(cmd)
    }

    public static func uninstall() throws {
        try runAsAdmin(shellQuote(SystemPaths.novactl) + " uninstall")
    }

    static func runAsAdmin(_ shell: String) throws {
        let escaped = shell.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let source = "do shell script \"\(escaped)\" with administrator privileges with prompt \"VPN Nova устанавливает фоновую службу.\""
        try run("/usr/bin/osascript", ["-e", source])
    }

    static func shellQuote(_ s: String) -> String {
        "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}

/// Unprivileged core for tests: runs sing-box as a child process.
public final class LocalCore: CoreService {
    let binary: String
    let workDir: URL
    var process: Process?
    public let logURL: URL

    public init(binary: String, workDir: URL) {
        self.binary = binary
        self.workDir = workDir
        logURL = workDir.appendingPathComponent("sing-box.log")
    }

    var configURL: URL { workDir.appendingPathComponent("config.json") }

    public func apply(config: Data) throws {
        try FileManager.default.createDirectory(at: workDir, withIntermediateDirectories: true)
        try config.write(to: configURL)
        try run(binary, ["check", "-c", configURL.path])
    }

    public func start() throws {
        try stop()
        FileManager.default.createFile(atPath: logURL.path, contents: nil)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: binary)
        p.arguments = ["run", "-c", configURL.path, "-D", workDir.path]
        let log = try FileHandle(forWritingTo: logURL)
        p.standardOutput = log
        p.standardError = log
        try p.run()
        process = p
    }

    public func stop() throws {
        guard let p = process else { return }
        if p.isRunning {
            p.terminate()
            p.waitUntilExit()
        }
        process = nil
    }

    public func isRunning() -> Bool { process?.isRunning == true }
}
