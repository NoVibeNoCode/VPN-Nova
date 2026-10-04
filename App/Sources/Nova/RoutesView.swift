import AppKit
import NovaCore
import SwiftUI

/// Live map: sources (apps / domains) → servers, with particles flowing along the routes.
struct RoutesView: View {
    @EnvironmentObject var vpn: VPNController

    struct Source: Identifiable {
        let id: String
        let name: String
        let subtitle: String
        let icon: NSImage?
        let target: TargetID
        let count: Int
    }

    struct Node: Identifiable {
        let id: TargetID
        let title: String
        let subtitle: String
        let color: Color
        let count: Int
        let isDefault: Bool
        let down: Bool
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            PageHeader(title: "Маршруты", subtitle: "Куда сейчас идёт трафик — в реальном времени") {
                if vpn.status == .on {
                    HStack(spacing: 8) { PulseDot(); Text("Live").font(Theme.body(13, .bold)) }
                        .foregroundStyle(Theme.mint)
                        .padding(.horizontal, 14).padding(.vertical, 8)
                        .background(Capsule().fill(Theme.mint.opacity(0.1)))
                }
            }
            LiveMetrics(stats: vpn.stats, live: vpn.live, blocked: blockedCount)
            if !vpn.otherVPNs.isEmpty {
                Banner(text: "Включён другой VPN: \(vpn.otherVPNs.joined(separator: ", ")). Он перехватывает трафик раньше Nova — выключи его в его приложении или в Системных настройках → VPN.", color: Theme.amber)
            }
            if vpn.status == .on {
                LiveMap(live: vpn.live)
            } else {
                emptyState
            }
        }
        .padding(.horizontal, 36)
        .padding(.vertical, 28)
    }

    private var blockedCount: Int {
        vpn.profile.settings.killSwitch ? vpn.profile.servers.filter { vpn.health[$0.id]?.isDown == true }.count : 0
    }

    private var emptyState: some View {
        VStack(spacing: 14) {
            Image(systemName: "point.3.connected.trianglepath.dotted")
                .font(.system(size: 44, weight: .light))
                .foregroundStyle(Theme.violet)
            Text("Включи VPN, чтобы увидеть живую карту").font(Theme.display(17, .bold))
            Text("Здесь появятся приложения и сайты и то, через какой сервер они идут.")
                .font(Theme.body(14)).foregroundStyle(Theme.muted)
            Button("Включить") { vpn.connect() }
                .buttonStyle(NovaButtonStyle(prominent: true))
                .disabled(vpn.status != .off && !isError)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var isError: Bool {
        if case .error = vpn.status { return true }
        return false
    }
}

/// Metric tiles; re-render on traffic/connection updates without touching the rest of the page.
struct LiveMetrics: View {
    @ObservedObject var stats: LiveStats
    @ObservedObject var live: LiveConnections
    let blocked: Int

    var body: some View {
        let (d, du) = formatSpeed(stats.down)
        let (u, uu) = formatSpeed(stats.up)
        HStack(spacing: 14) {
            Metric(title: "Соединений", value: "\(live.connections.count)", unit: "")
            Metric(title: "Загрузка", value: d, unit: du)
            Metric(title: "Отдача", value: u, unit: uu)
            Metric(title: "Заблокировано kill switch", value: "\(blocked)",
                   unit: plural(blocked, "сервер", "сервера", "серверов").split(separator: " ").dropFirst().joined(separator: " "))
        }
    }
}

/// The map itself: rebuilt only when the connection list changes.
struct LiveMap: View {
    @EnvironmentObject var vpn: VPNController
    @ObservedObject var live: LiveConnections

    var body: some View {
        let (sources, nodes) = model()
        VStack(alignment: .leading, spacing: 20) {
            if ConnectionInfo.processLookupBroken(live.connections) {
                Banner(text: "Ядро не видит, каким приложениям принадлежат соединения — правила по приложениям сейчас не срабатывают. Домены и «всё остальное» работают. Подробности в логах.", color: Theme.amber)
            }
            FlowMap(sources: sources, nodes: nodes)
        }
    }

    private func model() -> ([RoutesView.Source], [RoutesView.Node]) {
        let ids = vpn.profile.servers.map(\.id)
        var groups: [String: (name: String, sub: String, icon: NSImage?, targets: [TargetID: Int], count: Int)] = [:]
        var counts: [TargetID: Int] = [:]
        for c in live.connections {
            let target = ConnectionInfo.target(chains: c.chains, serverIDs: ids)
            counts[target, default: 0] += 1
            let key: String, name: String, sub: String
            var icon: NSImage?
            if let path = c.metadata.processPath, !path.isEmpty {
                let bundle = Naming.bundlePath(containing: path) ?? path
                key = bundle
                name = Naming.displayName(bundle)
                sub = "приложение"
                icon = IconCache.icon(for: bundle)
            } else {
                let host = c.metadata.host?.isEmpty == false ? c.metadata.host! : (c.metadata.destinationIP ?? "?")
                key = host
                name = host
                sub = "домен"
            }
            var g = groups[key] ?? (name, sub, icon, [:], 0)
            g.targets[target, default: 0] += 1
            g.count += 1
            groups[key] = g
        }
        let order = { (t: TargetID) in ids.firstIndex(of: t) ?? ids.count }
        let sources = groups.map { key, g in
            RoutesView.Source(id: key, name: g.name, subtitle: "\(g.sub) · \(g.count)", icon: g.icon,
                              target: g.targets.max { $0.value < $1.value }?.key ?? Target.direct, count: g.count)
        }
        // Busiest 7, then grouped by destination (fewer crossing lines) and by name,
        // so cards keep their places between polls instead of jumping around.
        .sorted { $0.count != $1.count ? $0.count > $1.count : $0.name < $1.name }
        .prefix(7)
        .sorted { a, b in
            let ia = order(a.target), ib = order(b.target)
            return ia != ib ? ia < ib : a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
        }

        let def = vpn.profile.settings.defaultTarget
        var nodes = vpn.profile.servers.map { s -> RoutesView.Node in
            let h = vpn.health[s.id]
            let delay = h?.delayMs.map { " · \($0) мс" } ?? ""
            return RoutesView.Node(id: s.id, title: s.code, subtitle: s.name + delay, color: Color(hex: s.colorHex),
                                   count: counts[s.id] ?? 0, isDefault: def == s.id, down: h?.isDown == true)
        }
        nodes.append(RoutesView.Node(id: Target.direct, title: "DIRECT", subtitle: "Напрямую", color: Theme.directGray,
                                     count: counts[Target.direct] ?? 0, isDefault: def == Target.direct, down: false))
        return (Array(sources), nodes)
    }
}

/// App icons are slow to fetch (IconServices); fetch each once.
enum IconCache {
    private static let cache = NSCache<NSString, NSImage>()

    static func icon(for path: String) -> NSImage {
        if let hit = cache.object(forKey: path as NSString) { return hit }
        let img = NSWorkspace.shared.icon(forFile: path)
        img.size = NSSize(width: 64, height: 64)
        cache.setObject(img, forKey: path as NSString)
        return img
    }
}

struct Metric: View {
    let title: String
    let value: String
    let unit: String

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(Theme.body(12)).foregroundStyle(Theme.muted)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(value).font(Theme.display(24, .bold)).monospacedDigit()
                Text(unit).font(Theme.body(12)).foregroundStyle(Theme.muted)
            }
        }
        .padding(.horizontal, 16).padding(.vertical, 14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 16).fill(Theme.panel))
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(Color.white.opacity(0.07)))
    }
}

struct Banner: View {
    let text: String
    let color: Color

    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(color)
            Text(text).font(Theme.body(13, .medium)).fixedSize(horizontal: false, vertical: true)
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14).fill(color.opacity(0.1)))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(color.opacity(0.35)))
    }
}

struct FlowMap: View {
    let sources: [RoutesView.Source]
    let nodes: [RoutesView.Node]

    private let sourceWidth: CGFloat = 230
    private let sourceHeight: CGFloat = 58
    private let nodeWidth: CGFloat = 240

    var body: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let h = geo.size.height
            let srcY = positions(count: sources.count, height: h, item: sourceHeight)
            let nodeH = max(36, min(120, (h - CGFloat(nodes.count - 1) * 10) / CGFloat(max(nodes.count, 1))))
            let nodeY = positions(count: nodes.count, height: h, item: nodeH)
            let routes: [FlowRoute] = sources.enumerated().compactMap { i, s in
                guard let ni = nodes.firstIndex(where: { $0.id == s.target }) else { return nil }
                return FlowRoute(from: CGPoint(x: sourceWidth, y: srcY[i]), to: CGPoint(x: w - nodeWidth, y: nodeY[ni]),
                                 color: nodes[ni].color, particles: min(1 + s.count / 3, 4))
            }

            ZStack(alignment: .topLeading) {
                FlowLayer(routes: routes)
                    .frame(width: w, height: h)
                    .allowsHitTesting(false)

                ForEach(Array(sources.enumerated()), id: \.element.id) { i, s in
                    SourceCard(source: s)
                        .frame(width: sourceWidth, height: sourceHeight)
                        .offset(x: 0, y: srcY[i] - sourceHeight / 2)
                        .transition(.opacity)
                }

                ForEach(Array(nodes.enumerated()), id: \.element.id) { i, n in
                    NodeCard(node: n)
                        .frame(width: nodeWidth, height: nodeH)
                        .offset(x: w - nodeWidth, y: nodeY[i] - nodeH / 2)
                }

                if sources.isEmpty {
                    Text("Пока тихо — соединений нет")
                        .font(Theme.body(14)).foregroundStyle(Theme.muted)
                        .frame(width: max(w - sourceWidth - nodeWidth, 10), height: h)
                        .offset(x: sourceWidth)
                }
            }
        }
    }

    private func positions(count: Int, height: CGFloat, item: CGFloat) -> [CGFloat] {
        guard count > 0 else { return [] }
        let gap = count > 1 ? min(30, (height - CGFloat(count) * item) / CGFloat(count - 1)) : 0
        let total = CGFloat(count) * item + CGFloat(count - 1) * gap
        let top = (height - total) / 2
        return (0..<count).map { top + CGFloat($0) * (item + gap) + item / 2 }
    }

}

struct SourceCard: View {
    let source: RoutesView.Source

    var body: some View {
        HStack(spacing: 12) {
            if let icon = source.icon {
                Image(nsImage: icon).resizable().frame(width: 34, height: 34)
            } else {
                Image(systemName: "globe")
                    .frame(width: 34, height: 34)
                    .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.06)))
                    .foregroundStyle(Theme.soft)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(source.name).font(Theme.body(14, .bold)).lineLimit(1)
                Text(source.subtitle).font(Theme.body(12)).foregroundStyle(Theme.muted)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 14)
        .background(RoundedRectangle(cornerRadius: 14).fill(Theme.panel))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.white.opacity(0.08)))
    }
}

struct NodeCard: View {
    let node: RoutesView.Node

    var body: some View {
        GeometryReader { geo in
            Group {
                if geo.size.height < 92 { compact } else { full }
            }
            .frame(width: geo.size.width, height: geo.size.height, alignment: .leading)
        }
        .background(
            RoundedRectangle(cornerRadius: 18)
                .fill(LinearGradient(colors: [node.color.opacity(0.85), node.color.opacity(0.25)], startPoint: .topLeading, endPoint: .bottomTrailing))
        )
        .overlay(BreathingRim(cornerRadius: 18, active: node.count > 0).allowsHitTesting(false))
        .shadow(color: node.color.opacity(node.count > 0 ? 0.4 : 0.12), radius: 22)
    }

    /// One line, for many servers on a short map.
    private var compact: some View {
        HStack(spacing: 10) {
            Text(node.title).font(Theme.display(node.title.count > 3 ? 14 : 17, .heavy)).lineLimit(1).fixedSize()
            VStack(alignment: .leading, spacing: 0) {
                Text(node.subtitle).font(Theme.body(11, .bold)).lineLimit(1)
                Text(node.down ? "недоступен" : plural(node.count, "соединение", "соединения", "соединений"))
                    .font(Theme.body(10)).foregroundStyle(.white.opacity(0.75)).lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
    }

    private var full: some View {
        VStack(alignment: .leading) {
            HStack(alignment: .top) {
                Text(node.title).font(Theme.display(node.title.count > 3 ? 24 : 32, .heavy))
                Spacer()
                if node.down {
                    Text("недоступен").font(Theme.body(11, .bold))
                        .padding(.horizontal, 8).padding(.vertical, 4)
                        .background(Capsule().fill(Theme.danger.opacity(0.3)))
                } else if node.isDefault {
                    Text("по умолчанию").font(Theme.body(11, .bold))
                        .padding(.horizontal, 8).padding(.vertical, 4)
                        .background(Capsule().fill(Color.white.opacity(0.14)))
                }
            }
            Spacer(minLength: 0)
            Text(node.subtitle).font(Theme.body(14, .bold)).lineLimit(1)
            Text(plural(node.count, "соединение", "соединения", "соединений")).font(Theme.body(12)).foregroundStyle(.white.opacity(0.7))
        }
        .padding(.horizontal, 18).padding(.vertical, 14)
    }
}

enum AppInfo {
    static func bundlePath(containing path: String) -> String? { Naming.bundlePath(containing: path) }
    static func usesSharedWebKit(_ path: String) -> Bool { Naming.usesSharedWebKit(path) }
    static func displayName(_ path: String) -> String { Naming.displayName(path) }
}
