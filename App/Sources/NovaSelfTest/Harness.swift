import Foundation
import NovaCore

/// Tiny test harness: named sections, pass/fail counters, shell helpers.
enum T {
    static var failures = 0
    static var passes = 0
    static var skips = 0
    static var singBox = ""
    static var work: URL!

    static func section(_ title: String) {
        print("\n\(title)")
    }

    static func check(_ cond: Bool, _ name: String, _ detail: @autoclosure () -> String = "") {
        if cond {
            passes += 1
            print("  ✓ \(name)")
        } else {
            failures += 1
            let d = detail()
            print("  ✗ \(name)\(d.isEmpty ? "" : "\n      " + d.replacingOccurrences(of: "\n", with: "\n      "))")
        }
    }

    static func skip(_ name: String, _ why: String) {
        skips += 1
        print("  – \(name): \(why)")
    }

    @discardableResult
    static func shell(_ exe: String, _ args: [String], timeout: TimeInterval = 60) -> (Int32, String) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = pipe
        do { try p.run() } catch { return (-1, "\(error)") }
        let deadline = Date().addingTimeInterval(timeout)
        var data = Data()
        let reader = DispatchQueue(label: "reader")
        let done = DispatchSemaphore(value: 0)
        reader.async {
            data = pipe.fileHandleForReading.readDataToEndOfFile()
            done.signal()
        }
        while p.isRunning && Date() < deadline { usleep(20_000) }
        if p.isRunning { p.terminate() }
        done.wait()
        p.waitUntilExit()
        return (p.terminationStatus, String(data: data, encoding: .utf8) ?? "")
    }

    /// Runs `sing-box check` on a config built from `profile`.
    static func singBoxCheck(_ data: Data, name: String) -> (Bool, String) {
        let url = work.appendingPathComponent("check-\(name).json")
        try? data.write(to: url)
        let (code, out) = shell(singBox, ["check", "-c", url.path])
        return (code == 0, out)
    }

    static func curl(_ url: String, proxyPort: Int, timeout: Int = 8) -> Bool {
        shell("/usr/bin/curl", ["-s", "-o", "/dev/null", "-m", String(timeout), "-x", "http://127.0.0.1:\(proxyPort)", url]).0 == 0
    }

    static func waitFor(_ attempts: Int = 50, _ cond: () async -> Bool) async -> Bool {
        for _ in 0..<attempts {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        return false
    }
}

/// A sing-box process running as a local "VPN server" for end-to-end tests.
final class TestServer {
    let process = Process()
    let log: URL

    init(config: JSONValue, name: String) throws {
        let dir = T.work.appendingPathComponent("server-\(name)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let cfg = dir.appendingPathComponent("config.json")
        let enc = JSONEncoder()
        try enc.encode(config).write(to: cfg)
        log = dir.appendingPathComponent("log.txt")
        FileManager.default.createFile(atPath: log.path, contents: nil)
        process.executableURL = URL(fileURLWithPath: T.singBox)
        process.arguments = ["run", "-c", cfg.path, "-D", dir.path]
        let h = try FileHandle(forWritingTo: log)
        process.standardOutput = h
        process.standardError = h
        try process.run()
    }

    func stop() {
        if process.isRunning {
            process.terminate()
            process.waitUntilExit()
        }
    }

    var logText: String { (try? String(contentsOf: log, encoding: .utf8)) ?? "" }
}
