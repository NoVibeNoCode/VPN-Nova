import AppKit
import Foundation
import NovaCore
import ServiceManagement

/// App state: profile, core lifecycle, live stats and the kill switch loop.
@MainActor
final class VPNController: ObservableObject {
    enum Status: Equatable {
        case notInstalled
        case needsUpdate
        case off
        case connecting
        case on
        case error(String)
    }

    static let appVersion = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    /// Version of the root part (core + scripts). Changes only when they change, so a new app
    /// version doesn't ask for the admin password unless the service really needs updating.
    static let serviceVersion = Bundle.main.object(forInfoDictionaryKey: "NovaServiceVersion") as? String ?? appVersion

    @Published private(set) var profile: Profile
    @Published private(set) var status: Status = .off
    @Published private(set) var connectedSince: Date?
    /// Per-second traffic and the live connection list change constantly; they live in their own
    /// objects so only the views showing them re-render (not the whole menu and window).
    let stats = LiveStats()
    let live = LiveConnections()
    var down: Int { stats.down }
    var up: Int { stats.up }
    @Published private(set) var health: [String: HealthMonitor.ServerHealth] = [:]
    @Published private(set) var delayHistory: [String: [Int]] = [:]
    var connections: [ClashAPI.Connection] { live.connections }
    @Published private(set) var applying = false
    @Published var lastError: String?
    /// Other VPN clients that are connected right now (they intercept traffic before Nova).
    @Published private(set) var otherVPNs: [String] = []

    private let core = PrivilegedCore(stagingDir: ProfileStore.directory)
    /// Demo controller for `--snapshot` / `--demo`: fake live data, never touches disk or the core.
    private(set) var isDemo = false
    private var demoTimer: Timer?
    private var loops: [Task<Void, Never>] = []
    private var applyTask: Task<Void, Never>?
    private var monitor: HealthMonitor?

    init() {
        profile = ProfileStore.load()
        lastError = ProfileStore.loadError
        // Persist right away: the Clash API secret must survive app restarts.
        if !FileManager.default.fileExists(atPath: ProfileStore.profileURL.path) {
            try? ProfileStore.save(profile)
        }
        refreshInstallState()
        if status == .off {
            Task { await attachIfRunning() }
        }
    }

    func demoSetStatus(_ s: Status) { status = s }

    /// Puts a demo controller into an arbitrary state for snapshot checks.
    func demoSet(status: Status? = nil, profile: Profile? = nil, health: [String: HealthMonitor.ServerHealth]? = nil,
                 otherVPNs: [String]? = nil, lastError: String? = nil, connections: [ClashAPI.Connection]? = nil) {
        guard isDemo else { return }
        if let status { self.status = status }
        if let profile { self.profile = profile }
        if let health { self.health = health }
        if let otherVPNs { self.otherVPNs = otherVPNs }
        if let lastError { self.lastError = lastError }
        if let connections { live.connections = connections }
    }

    /// Demo state for `--snapshot` renders; never touches disk or the core.
    init(demo: Bool) {
        isDemo = true
        var p = Profile()
        let de = Server(id: "de", name: "Германия", code: "DE", colorHex: "#8B7CFF",
                        outbound: ["type": "vless", "server": "203.0.113.10", "server_port": 443, "flow": "xtls-rprx-vision",
                                   "tls": ["enabled": true, "reality": ["enabled": true]]])
        let ru = Server(id: "ru", name: "Россия", code: "RU", colorHex: "#FF7A59",
                        outbound: ["type": "shadowsocks", "server": "198.51.100.20", "server_port": 8388])
        p.servers = [de, ru]
        p.domainRules = [
            DomainRule(kind: .domain, value: "youtube.com", target: "de"),
            DomainRule(kind: .domain, value: ".ru", target: "ru"),
            DomainRule(kind: .ruleset, value: "geosite-cn,geoip-cn", target: Target.direct),
            DomainRule(kind: .keyword, value: "openai", target: "de"),
        ]
        p.appRules = [
            AppRule(name: "Telegram", path: "/Applications/Telegram.app", target: "de"),
            AppRule(name: "Claude", path: "/Applications/Claude.app", target: "de"),
            AppRule(name: "Safari", path: "/Applications/Safari.app", target: "ru"),
        ]
        p.settings.defaultTarget = "de"
        profile = p
        status = .on
        connectedSince = Date().addingTimeInterval(-8057)
        stats.down = 4_200_000
        stats.up = 310_000
        stats.downHistory = (0..<32).map { i in 1_500_000 + 2_500_000 * abs(sin(Double(i) * 0.7)) }
        stats.upHistory = (0..<32).map { i in 120_000 + 200_000 * abs(cos(Double(i) * 0.5)) }
        health = ["de": .init(delayMs: 42), "ru": .init(delayMs: 18)]
        delayHistory = ["de": [48, 44, 51, 40, 46, 39, 42, 45, 41, 38, 44, 42], "ru": [20, 19, 22, 18, 17, 21, 18, 19, 18, 17, 19, 18]]
        let sample: [(String, String?, String)] = [
            ("www.youtube.com", nil, "via-de"), ("i.ytimg.com", nil, "via-de"), ("yandex.ru", nil, "via-ru"),
            ("api.telegram.org", "/Applications/Telegram.app/Contents/MacOS/Telegram", "via-de"),
            ("149.154.167.51", "/Applications/Telegram.app/Contents/MacOS/Telegram", "via-de"),
            ("claude.ai", "/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper", "via-de"),
            ("ya.ru", "/Applications/Safari.app/Contents/MacOS/Safari", "via-ru"),
            ("baidu.com", nil, "direct"), ("github.com", nil, "via-de"),
        ]
        let json = "{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":[" + sample.enumerated().map { i, s in
            let path = s.1.map { "\"processPath\":\"\($0)\"," } ?? ""
            return "{\"id\":\"\(i)\",\"upload\":0,\"download\":0,\"chains\":[\"\(s.2)\"],\"metadata\":{\(path)\"network\":\"tcp\",\"host\":\"\(s.0)\"}}"
        }.joined(separator: ",") + "]}"
        live.connections = (try? JSONDecoder().decode(ClashAPI.Connections.self, from: Data(json.utf8)))?.connections ?? []
        demoTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.demoTick() }
        }
    }

    private func demoTick() {
        guard status == .on, ProcessInfo.processInfo.environment["NOVA_DEMO_STATIC"] == nil else { return }
        push(down: Int.random(in: 900_000...5_800_000), up: Int.random(in: 90_000...420_000))
        for id in ["de", "ru"] {
            var h = health[id] ?? .init()
            h.delayMs = (id == "de" ? 38 : 16) + Int.random(in: 0...12)
            health[id] = h
            delayHistory[id] = Array(((delayHistory[id] ?? []) + [h.delayMs ?? 0]).suffix(24))
        }
    }

    var api: ClashAPI { ClashAPI(port: profile.settings.clashPort, secret: profile.clashSecret) }
    var isActive: Bool { status == .on || status == .connecting }

    /// Some server that has traffic routed to it is down.
    var hasDownServer: Bool {
        status == .on && profile.servers.contains { health[$0.id]?.isDown == true && isUsed($0.id) }
    }

    func isUsed(_ id: String) -> Bool {
        profile.settings.defaultTarget == id
            || profile.domainRules.contains { $0.enabled && $0.target == id }
            || profile.appRules.contains { $0.enabled && $0.target == id }
    }

    // MARK: - Install

    func refreshInstallState() {
        if !PrivilegedCore.isInstalled {
            status = .notInstalled
        } else if PrivilegedCore.installedVersion != Self.serviceVersion {
            status = .needsUpdate
        } else if case .notInstalled = status {
            status = .off
        } else if case .needsUpdate = status {
            status = .off
        }
    }

    func install() {
        if isDemo { status = .off; return }
        guard let res = Bundle.main.resourceURL else { return }
        let wasOn = isActive
        Task {
            do {
                try await Self.background { try PrivilegedCore.install(resources: res, version: Self.serviceVersion) }
                lastError = nil
                status = .off
                refreshInstallState()
                if wasOn { connect() }
            } catch {
                lastError = "Установка не удалась: \(error.localizedDescription)"
            }
        }
    }

    func uninstall() {
        Task {
            stopLoops()
            do {
                try await Self.background { try PrivilegedCore.uninstall() }
                status = .notInstalled
            } catch {
                lastError = "Удаление не удалось: \(error.localizedDescription)"
            }
        }
    }

    // MARK: - Connect

    func toggle() {
        if isDemo {
            if isActive {
                status = .off
                connectedSince = nil
            } else {
                status = .connecting
                DispatchQueue.main.asyncAfter(deadline: .now() + 1.6) { [weak self] in
                    self?.status = .on
                    self?.connectedSince = Date()
                }
            }
            return
        }
        switch status {
        case .on, .connecting: disconnect()
        case .off, .error: connect()
        case .notInstalled, .needsUpdate: install()
        }
    }

    func refreshOtherVPNs() {
        guard !isDemo else { return }
        Task {
            let names = await Self.backgroundValue { SystemNetwork.connectedVPNs() } ?? []
            if names != otherVPNs { otherVPNs = names }
        }
    }

    /// Bumped by every connect / disconnect / apply; a finishing operation that has been
    /// superseded must not touch the state.
    private var opID = 0

    func connect() {
        guard status != .notInstalled, status != .needsUpdate else { return }
        refreshOtherVPNs()
        applyTask?.cancel()
        opID += 1
        let op = opID
        status = .connecting
        lastError = nil
        let core = core
        Task {
            do {
                let config = try await makeConfig()
                guard op == opID else { return }
                try await Self.onCoreQueue {
                    try core.apply(config: config)
                    try core.start()
                }
                guard op == opID else { return }
                if await waitForAPI(), op == opID {
                    connectedSince = Date()
                    status = .on
                    startLoops()
                } else if op == opID {
                    // Don't leave launchd restarting a core that can't come up.
                    try? await Self.onCoreQueue { try core.stop() }
                    status = .error("Ядро не запустилось. Загляни в логи.")
                }
            } catch {
                guard op == opID else { return }
                status = .error(Self.short(error))
            }
        }
    }

    func disconnect() {
        stopLoops()
        applyTask?.cancel()
        opID += 1
        let op = opID
        let core = core
        Task {
            do {
                try await Self.onCoreQueue { try core.stop() }
                guard op == opID else { return }
                status = .off
            } catch {
                guard op == opID else { return }
                status = .error(Self.short(error))
            }
            connectedSince = nil
            stats.reset()
            live.connections = []
            health = [:]
        }
    }

    func quit() {
        stopLoops()
        if isActive {
            let core = core
            Self.coreQueue.sync { try? core.stop() }
        }
        NSApp.terminate(nil)
    }

    private func attachIfRunning() async {
        let core = core
        let running = await Self.backgroundValue { core.isRunning() }
        guard running == true else {
            if profile.settings.connectOnLaunch { connect() }
            return
        }
        if await waitForAPI(attempts: 10) {
            connectedSince = Date()
            status = .on
            startLoops()
        }
    }

    private func waitForAPI(attempts: Int = 60) async -> Bool {
        let api = api
        for _ in 0..<attempts {
            if await api.isAlive() { return true }
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        return false
    }

    // MARK: - Profile edits

    /// Mutates the profile, saves it, and restarts the core (debounced) when needed.
    func update(restart: Bool = true, _ change: (inout Profile) -> Void) {
        change(&profile)
        if isDemo { return }
        do {
            try ProfileStore.save(profile)
        } catch {
            lastError = "Не удалось сохранить профиль: \(error.localizedDescription)"
        }
        if restart, status == .on { scheduleApply() }
    }

    private func scheduleApply() {
        applyTask?.cancel()
        applyTask = Task {
            try? await Task.sleep(nanoseconds: 700_000_000)
            guard !Task.isCancelled else { return }
            await applyNow()
        }
    }

    private func applyNow() async {
        guard status == .on else { return }
        opID += 1
        let op = opID
        applying = true
        status = .connecting
        defer { applying = false }
        stopLoops()
        let core = core
        do {
            let config = try await makeConfig()
            guard op == opID else { return }
            try await Self.onCoreQueue {
                try core.apply(config: config)
                try core.start()
            }
            guard op == opID else { return }
            if await waitForAPI(), op == opID {
                status = .on
                startLoops()
            } else if op == opID {
                status = .error("Ядро не перезапустилось. Загляни в логи.")
            }
        } catch {
            guard op == opID else { return }
            // The new config was rejected; the core keeps running the previous one.
            lastError = Self.short(error)
            if await waitForAPI(attempts: 5), op == opID {
                status = .on
                startLoops()
            } else if op == opID {
                status = .error(Self.short(error))
            }
        }
    }

    /// Builds the TUN config from local rule-set files (shipped ones are seeded first): no network wait.
    /// Missing or stale sets are fetched in the background afterwards.
    private func makeConfig() async throws -> Data {
        let store = RuleSetStore()
        let profile = profile
        let data = try await Self.background { () throws -> Data in
            store.seed(from: Bundle.main.resourceURL?.appendingPathComponent("rule-sets"))
            var builder = ConfigBuilder(profile: profile)
            builder.ruleSetDirectory = store.directory
            return try builder.data()
        }
        refreshRuleSetsInBackground()
        return data
    }

    private var ruleSetRefresh: Task<Void, Never>?

    private func refreshRuleSetsInBackground() {
        guard ruleSetRefresh == nil else { return }
        let store = RuleSetStore()
        let tags = RuleSetStore.tags(in: profile)
        let missing = store.missing(tags)
        let need = missing + (profile.settings.autoUpdateRuleSets ? store.stale(tags) : [])
        guard !need.isEmpty else { return }
        ruleSetRefresh = Task { [weak self] in
            let failed = await store.ensure(need, refresh: true)
            guard let self else { return }
            self.ruleSetRefresh = nil
            let fetched = missing.filter { failed[$0] == nil }
            if !failed.isEmpty {
                self.lastError = "Не скачались наборы: \(failed.keys.sorted().joined(separator: ", ")). Их правила пропущены."
            }
            // Newly available sets take effect with a quick core restart.
            if !fetched.isEmpty, self.status == .on { self.scheduleApply() }
        }
    }

    /// "Всё остальное" switches live through the default-route selector.
    func setDefaultTarget(_ target: TargetID) {
        update(restart: false) { $0.settings.defaultTarget = target }
        guard status == .on, !isDemo else { return }
        let api = api
        let tag = ConfigBuilder(profile: profile).outboundTag(target)
        Task {
            try? await api.select(ConfigBuilder.defaultRouteTag, tag)
        }
    }

    func setKillSwitch(_ on: Bool) {
        update(restart: false) { $0.settings.killSwitch = on }
        guard status == .on, !isDemo, let monitor else { return }
        let ids = profile.servers.map(\.id)
        Task {
            health = await monitor.tick(serverIDs: ids, killSwitch: on)
            await api.closeAllConnections()
        }
    }

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
            update(restart: false) { $0.settings.launchAtLogin = on }
        } catch {
            lastError = "Автозапуск: \(error.localizedDescription)"
        }
    }

    // MARK: - Live loops

    private func startLoops() {
        stopLoops()
        let api = api
        let monitor = HealthMonitor(api: api)
        self.monitor = monitor

        loops.append(Task { [weak self] in
            while !Task.isCancelled {
                do {
                    for try await t in api.traffic() {
                        guard let self else { return }
                        self.push(down: t.down, up: t.up)
                    }
                } catch {}
                try? await Task.sleep(nanoseconds: 1_000_000_000)
            }
        })

        loops.append(Task { [weak self] in
            var tick = 0
            while !Task.isCancelled {
                guard let self else { return }
                if let c = try? await api.connections() {
                    let list = c.connections ?? []
                    // Skip identical snapshots: no re-render when nothing changed.
                    if list.map(\.id) != self.live.connections.map(\.id) { self.live.connections = list }
                } else if !(await api.isAlive()) {
                    self.coreDied()
                    return
                }
                if tick % 5 == 0 {
                    self.refreshOtherVPNs()
                    let ids = self.profile.servers.map(\.id)
                    let h = await monitor.tick(serverIDs: ids, killSwitch: self.profile.settings.killSwitch)
                    self.health = h
                    for (id, s) in h {
                        var hist = self.delayHistory[id] ?? []
                        hist.append(s.delayMs ?? 0)
                        self.delayHistory[id] = Array(hist.suffix(24))
                    }
                }
                tick += 1
                try? await Task.sleep(nanoseconds: 2_000_000_000)
            }
        })
    }

    private func stopLoops() {
        loops.forEach { $0.cancel() }
        loops = []
    }

    private func coreDied() {
        stopLoops()
        status = .error("Ядро остановилось. Загляни в логи.")
        live.connections = []
        stats.reset()
    }

    private func push(down: Int, up: Int) {
        stats.push(down: down, up: up)
    }

    // MARK: - Diagnostics

    /// The core can't see which app owns a connection, so app rules can't match.
    var processLookupBroken: Bool { ConnectionInfo.processLookupBroken(connections) }

    /// Target a live connection actually went to.
    func target(of c: ClashAPI.Connection) -> TargetID {
        ConnectionInfo.target(chains: c.chains, serverIDs: profile.servers.map(\.id))
    }

    // MARK: - Helpers

    /// Core start/stop/apply run one at a time, in the order they were requested.
    private static let coreQueue = DispatchQueue(label: "nova.core", qos: .userInitiated)

    nonisolated static func onCoreQueue(_ work: @escaping () throws -> Void) async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            coreQueue.async { cont.resume(with: Result { try work() }) }
        }
    }

    nonisolated static func background<T>(_ work: @escaping () throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { cont in
            DispatchQueue.global(qos: .userInitiated).async {
                cont.resume(with: Result { try work() })
            }
        }
    }

    nonisolated static func backgroundValue<T>(_ work: @escaping () -> T) async -> T? {
        try? await background { () throws -> T in work() }
    }

    static func short(_ error: Error) -> String {
        let text = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        return String(text.split(separator: "\n").last ?? Substring(text)).trimmingCharacters(in: .whitespaces)
    }
}

@MainActor
final class LiveStats: ObservableObject {
    @Published var down = 0
    @Published var up = 0
    @Published var downHistory = Array(repeating: 0.0, count: 24)
    @Published var upHistory = Array(repeating: 0.0, count: 24)

    func push(down: Int, up: Int) {
        self.down = down
        self.up = up
        downHistory = Array((downHistory + [Double(down)]).suffix(24))
        upHistory = Array((upHistory + [Double(up)]).suffix(24))
    }

    func reset() {
        down = 0
        up = 0
    }
}

@MainActor
final class LiveConnections: ObservableObject {
    @Published var connections: [ClashAPI.Connection] = []
}
