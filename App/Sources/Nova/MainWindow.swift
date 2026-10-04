import NovaCore
import SwiftUI

enum Section: String, CaseIterable, Identifiable {
    case routes, rules, servers, settings, logs
    var id: String { rawValue }

    var title: String {
        switch self {
        case .routes: return "Маршруты"
        case .rules: return "Правила"
        case .servers: return "Серверы"
        case .settings: return "Настройки"
        case .logs: return "Логи"
        }
    }

    var icon: String {
        switch self {
        case .routes: return "point.topleft.down.to.point.bottomright.curvepath"
        case .rules: return "list.bullet"
        case .servers: return "server.rack"
        case .settings: return "gearshape"
        case .logs: return "doc.text"
        }
    }
}

struct MainWindow: View {
    @EnvironmentObject var vpn: VPNController
    @State private var section: Section
    /// Pages are built on first visit and then kept alive: switching only flips visibility.
    @State private var visited: [Section]

    init(section: Section = .routes) {
        _section = State(initialValue: section)
        _visited = State(initialValue: [section])
    }

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider().overlay(Theme.line)
            ZStack {
                ForEach(visited) { s in
                    let active = s == section
                    page(s)
                        .opacity(active ? 1 : 0)
                        .allowsHitTesting(active)
                        .accessibilityHidden(!active)
                        .environment(\.novaPageActive, active)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipped()
        }
        .frame(minWidth: 980, minHeight: 600)
        .onReceive(NotificationCenter.default.publisher(for: .novaDemoNextSection)) { _ in
            let all = Section.allCases
            go(all[(all.firstIndex(of: section)! + 1) % all.count])
        }
        .pausesAnimationsWhenHidden()
        .background(Theme.bg)
        .foregroundStyle(Theme.text)
        .preferredColorScheme(.dark)
    }

    private func go(_ s: Section) {
        if !visited.contains(s) { visited.append(s) }
        section = s
    }

    @ViewBuilder private func page(_ s: Section) -> some View {
        switch s {
        case .routes: RoutesView()
        case .rules: RulesView()
        case .servers: ServersView()
        case .settings: SettingsView()
        case .logs: LogsView()
        }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 4) {
            (Text("Nova") + Text(".").foregroundColor(Theme.violet))
                .font(Theme.display(18, .heavy))
                .padding(.horizontal, 12)
                .padding(.top, 8)
                .padding(.bottom, 18)
            ForEach(Section.allCases) { s in
                Button {
                    go(s)
                } label: {
                    HStack(spacing: 10) {
                        Image(systemName: s.icon).frame(width: 18)
                        Text(s.title)
                        Spacer()
                    }
                    .font(Theme.body(14, .semibold))
                    .foregroundStyle(section == s ? Theme.text : Theme.muted)
                    .padding(.horizontal, 12)
                    .frame(height: 40)
                    .background(RoundedRectangle(cornerRadius: 10).fill(section == s ? Theme.violet.opacity(0.14) : .clear))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            Spacer()
            SidebarStatus()
        }
        .padding(14)
        .frame(width: 220)
        .background(Color.white.opacity(0.015))
    }
}

struct SidebarStatus: View {
    @EnvironmentObject var vpn: VPNController

    var body: some View {
        let on = vpn.status == .on
        HStack(spacing: 10) {
            PulseDot(color: on ? (vpn.hasDownServer ? Theme.amber : Theme.mint) : Theme.muted)
            VStack(alignment: .leading, spacing: 1) {
                Text(on ? "Активен" : vpn.status == .connecting ? "Подключаюсь" : "Выключен").font(Theme.body(13, .bold))
                Text("остальное → \(Theme.label(for: vpn.profile.settings.defaultTarget, in: vpn.profile))")
                    .font(Theme.body(12)).foregroundStyle(Theme.muted)
            }
            Spacer()
            Button {
                vpn.toggle()
            } label: {
                Image(systemName: "power").font(.system(size: 13, weight: .bold))
            }
            .buttonStyle(.plain)
            .foregroundStyle(on ? Theme.mint : Theme.muted)
            .accessibilityLabel(Text(on ? "Выключить" : "Включить"))
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 14).fill((on ? Theme.mint : Color.white).opacity(0.07)))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke((on ? Theme.mint : Color.white).opacity(0.18)))
    }
}

/// Page header used by every section.
struct PageHeader<Trailing: View>: View {
    let title: String
    let subtitle: String
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(alignment: .bottom) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(Theme.display(28, .heavy))
                Text(subtitle).font(Theme.body(14)).foregroundStyle(Theme.muted)
            }
            Spacer()
            trailing
        }
    }
}

extension PageHeader where Trailing == EmptyView {
    init(title: String, subtitle: String) {
        self.init(title: title, subtitle: subtitle) { EmptyView() }
    }
}

extension Notification.Name {
    /// Debug: `--demo <section> --cycle-sections` flips through sections to profile switching.
    static let novaDemoNextSection = Notification.Name("NovaDemoNextSection")
}

private struct NovaPageActiveKey: EnvironmentKey {
    static let defaultValue = true
}

extension EnvironmentValues {
    /// False for kept-alive pages that are not on screen; they skip polling.
    var novaPageActive: Bool {
        get { self[NovaPageActiveKey.self] }
        set { self[NovaPageActiveKey.self] = newValue }
    }
}
