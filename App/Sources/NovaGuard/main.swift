import Foundation
import NovaCore

// Root-side config check, run by novactl before a config reaches sing-box.
// Usage: novaguard <config.json> <allowed rule-set dir>   → exit 0 if allowed.
let args = CommandLine.arguments
guard args.count == 3, let data = FileManager.default.contents(atPath: args[1]) else {
    FileHandle.standardError.write("usage: novaguard <config> <rule-set dir>\n".data(using: .utf8)!)
    exit(2)
}
let errors = ConfigGuard.validate(data, ruleSetDirectory: args[2])
if errors.isEmpty { exit(0) }
FileHandle.standardError.write(("novaguard: конфиг отклонён: " + errors.prefix(5).joined(separator: "; ") + "\n").data(using: .utf8)!)
exit(1)
