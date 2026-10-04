import Foundation

/// Checks every server through the Clash API and applies the kill switch policy:
/// - kill switch ON: selectors always point at the server, a dead server blocks its traffic;
/// - kill switch OFF: a dead server's selector falls back to `direct` until it recovers.
public final class HealthMonitor {
    public struct ServerHealth: Equatable {
        public var delayMs: Int?
        public var failures: Int = 0
        public var fallbackActive = false
        public var isDown: Bool { failures >= HealthMonitor.failuresToMarkDown }

        public init(delayMs: Int? = nil, failures: Int = 0, fallbackActive: Bool = false) {
            self.delayMs = delayMs
            self.failures = failures
            self.fallbackActive = fallbackActive
        }
    }

    public static let failuresToMarkDown = 2

    let api: ClashAPI
    public private(set) var health: [String: ServerHealth] = [:]

    public init(api: ClashAPI) {
        self.api = api
    }

    /// One check round. Returns the new state per server id.
    @discardableResult
    public func tick(serverIDs: [String], killSwitch: Bool) async -> [String: ServerHealth] {
        await withTaskGroup(of: (String, Int?).self) { group in
            for id in serverIDs {
                group.addTask { (id, await self.api.delay(ConfigBuilder.serverTag(id), timeoutMs: 6000)) }
            }
            for await (id, delay) in group {
                var h = health[id] ?? ServerHealth()
                h.delayMs = delay
                h.failures = delay == nil ? h.failures + 1 : 0
                health[id] = h
            }
        }
        health = health.filter { serverIDs.contains($0.key) }

        for id in serverIDs {
            guard var h = health[id] else { continue }
            let wantFallback = !killSwitch && h.isDown
            let selector = ConfigBuilder.selectorTag(id)
            let want = wantFallback ? ConfigBuilder.directTag : ConfigBuilder.serverTag(id)
            if await api.selected(selector) != want {
                try? await api.select(selector, want)
            }
            h.fallbackActive = wantFallback
            health[id] = h
        }
        return health
    }
}
