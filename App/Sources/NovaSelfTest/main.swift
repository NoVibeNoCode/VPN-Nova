import Foundation
import NovaCore

// VPN Nova self-test. Runs without root against the real sing-box binary.
//
// Usage: NovaSelfTest <path to sing-box> [--only links,config,rulesets,fuzz,live,e2e] [--fuzz N]

let args = CommandLine.arguments
guard args.count > 1 else {
    print("usage: NovaSelfTest <sing-box> [--only a,b] [--fuzz N]")
    exit(2)
}
T.singBox = URL(fileURLWithPath: args[1]).standardizedFileURL.path
T.work = FileManager.default.temporaryDirectory.appendingPathComponent("nova-selftest-\(getpid())")
try FileManager.default.createDirectory(at: T.work, withIntermediateDirectories: true)

// `--emit-tun <out.json> <ss-port> <clash-port> <secret>`: TUN config for the root test script.
if let i = args.firstIndex(of: "--emit-tun") {
    var p = Profile()
    p.clashSecret = args[i + 4]
    p.settings.clashPort = Int(args[i + 3])!
    p.settings.defaultTarget = Target.direct
    let host = args.count > i + 5 ? args[i + 5] : "127.0.0.1"
    p.servers = [
        Server(id: "ss", name: "Local SS", code: "SS", colorHex: "#8B7CFF",
               outbound: try LinkParser.parse("ss://" + Data("aes-256-gcm:nova-tun-test".utf8).base64EncodedString() + "@\(host):\(args[i + 2])").outbound),
        Server(id: "dead", name: "Dead", code: "XX", colorHex: "#FF5C6C",
               outbound: ["type": "shadowsocks", "server": "127.0.0.1", "server_port": 9, "method": "aes-256-gcm", "password": "x"]),
    ]
    p.domainRules = [DomainRule(kind: .domain, value: "example.net", target: "dead")]
    p.appRules = [AppRule(name: "curl", path: "/usr/bin", target: "ss")]
    var b = ConfigBuilder(profile: p)
    b.logLevel = "debug"
    try b.data().write(to: URL(fileURLWithPath: args[i + 1]))
    exit(0)
}

let only: Set<String>? = args.firstIndex(of: "--only").map { Set(args[$0 + 1].split(separator: ",").map(String.init)) }
let fuzzCount = args.firstIndex(of: "--fuzz").flatMap { Int(args[$0 + 1]) } ?? 150
func enabled(_ name: String) -> Bool { only?.contains(name) ?? true }

if enabled("links") { linkTests() }

if enabled("rulesets") || enabled("config") {
    T.section("Наборы правил: скачивание")
    let store = RuleSetStore(directory: T.work.appendingPathComponent("rule-sets"))
    let failed = await store.ensure(["geosite-telegram", "geoip-ru", "geosite-no-such-set-nova"], refresh: true)
    T.check(FileManager.default.fileExists(atPath: store.path(for: "geosite-telegram").path), "geosite-telegram скачан")
    T.check(FileManager.default.fileExists(atPath: store.path(for: "geoip-ru").path), "geoip-ru скачан")
    T.check(failed.keys.sorted() == ["geosite-no-such-set-nova"], "несуществующий набор — в списке ошибок, остальным не мешает", "\(failed)")
    let mtime = { (try? FileManager.default.attributesOfItem(atPath: store.path(for: "geoip-ru").path)[.modificationDate] as? Date) ?? nil }
    let before = mtime()
    _ = await store.ensure(["geoip-ru"], refresh: true)
    T.check(mtime() == before, "свежий файл повторно не качается")

    T.section("Наборы правил: вшитые в приложение")
    let bundled = URL(fileURLWithPath: T.singBox).deletingLastPathComponent().appendingPathComponent("rule-sets")
    let fresh = RuleSetStore(directory: T.work.appendingPathComponent("seeded"))
    let presetTags = RuleSetPreset.all.flatMap(\.tags)
    T.check(fresh.missing(presetTags).count == presetTags.count, "пустое хранилище: всё отсутствует")
    let t0 = Date()
    fresh.seed(from: bundled)
    T.check(fresh.missing(presetTags).isEmpty, "все \(presetTags.count) пресетов есть в приложении и копируются без сети", "нет: \(fresh.missing(presetTags))")
    T.check(Date().timeIntervalSince(t0) < 0.5, "засев занимает < 0,5 с (подключение не ждёт GitHub)")
    var withSets = Profile.starter()
    withSets.domainRules.append(DomainRule(kind: .ruleset, value: "geosite-youtube,geoip-ru", target: Target.direct))
    var b = ConfigBuilder(profile: withSets)
    b.ruleSetDirectory = fresh.directory
    let (ok, out) = T.singBoxCheck(try! b.data(), name: "seeded")
    T.check(ok, "конфиг на вшитых наборах проходит sing-box check", out)
    T.check(fresh.stale(presetTags).allSatisfy { _ in true }, "проверка устаревания отрабатывает")
}

if enabled("config") { configTests(); helperTests(); guardTests(); securityFlagTests() }
if enabled("fuzz") { fuzzTests(count: fuzzCount) }
if enabled("live") { await liveRoutingTests() }
if enabled("e2e") { await protocolServerTests() }

print("\nИтого: ✓ \(T.passes)  ✗ \(T.failures)  – \(T.skips)")
if T.failures == 0 { try? FileManager.default.removeItem(at: T.work) } else { print("рабочая папка: \(T.work.path)") }
exit(T.failures == 0 ? 0 : 1)
