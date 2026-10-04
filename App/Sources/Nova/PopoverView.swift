import NovaCore
import SwiftUI

struct PopoverView: View {
    @EnvironmentObject var vpn: VPNController
    @Environment(\.openWindow) private var openWindow

    var body: some View {
        VStack(spacing: 12) {
            header
            orbSection
            if case .notInstalled = vpn.status {
                SetupCard()
            } else if case .needsUpdate = vpn.status {
                SetupCard(update: true)
            } else {
                speeds
                servers
                defaultRoute
            }
            if !vpn.otherVPNs.isEmpty {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(Theme.amber)
                    Text("Включён другой VPN: \(vpn.otherVPNs.joined(separator: ", ")). Выключи его, иначе трафик пойдёт мимо Nova.")
                        .font(Theme.body(12, .medium))
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 12).fill(Theme.amber.opacity(0.12)))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(Theme.amber.opacity(0.35)))
            }
            if let err = vpn.lastError {
                Text(err)
                    .font(Theme.body(12, .medium))
                    .foregroundStyle(Theme.danger)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            footer
        }
        .padding(16)
        .frame(width: 360)
        .background(
            ZStack {
                Theme.panel.opacity(0.97)
                Circle().fill(RadialGradient(colors: [Theme.violetDeep.opacity(0.35), .clear], center: .center, startRadius: 0, endRadius: 210))
                    .frame(width: 420, height: 420).offset(x: -140, y: -120)
                Circle().fill(RadialGradient(colors: [Theme.coral.opacity(0.16), .clear], center: .center, startRadius: 0, endRadius: 190))
                    .frame(width: 380, height: 380).offset(x: 170, y: 220)
            }
            .clipped()
        )
        .foregroundStyle(Theme.text)
        .preferredColorScheme(.dark)
        .pausesAnimationsWhenHidden()
        .onAppear { vpn.refreshOtherVPNs() }
    }

    private var header: some View {
        HStack {
            Text("VPN Nova").font(Theme.display(15, .bold))
            Spacer()
            StatusPill()
        }
    }

    private var orbSection: some View {
        VStack(spacing: 6) {
            NovaOrb(state: orbState) { vpn.toggle() }
                .frame(width: 156, height: 156)
                .disabled(vpn.status == .notInstalled || vpn.status == .needsUpdate)
            Text(headline).font(Theme.display(21, .bold))
            TimelineView(.periodic(from: .now, by: 1)) { ctx in
                Text(subline(at: ctx.date)).font(Theme.mono(12)).foregroundStyle(Theme.muted)
            }
        }
        .padding(.top, 4)
    }

    private var orbState: NovaOrb.OrbState {
        switch vpn.status {
        case .on: return vpn.hasDownServer ? .warning : .on
        case .connecting: return .connecting
        case .error: return .error
        default: return .off
        }
    }

    private var headline: String {
        switch vpn.status {
        case .on: return vpn.hasDownServer ? "Сервер недоступен" : "Защищено"
        case .connecting: return vpn.applying ? "Применяю…" : "Подключаюсь…"
        case .error: return "Ошибка"
        case .notInstalled: return "Нужна установка"
        case .needsUpdate: return "Нужно обновить"
        case .off: return "Выключено"
        }
    }

    private func subline(at date: Date) -> String {
        switch vpn.status {
        case .on:
            guard let since = vpn.connectedSince else { return "в сети" }
            let s = Int(date.timeIntervalSince(since))
            return String(format: "в сети %02d:%02d:%02d", s / 3600, s / 60 % 60, s % 60)
        case .connecting: return "запускаю ядро sing-box"
        case .error(let m): return m
        case .notInstalled, .needsUpdate: return "один раз, с паролем администратора"
        case .off: return "нажми, чтобы включить"
        }
    }

    private var speeds: some View {
        SpeedsRow(stats: vpn.stats, color: Theme.color(for: vpn.profile.settings.defaultTarget, in: vpn.profile), active: vpn.status == .on)
    }

    @ViewBuilder private var servers: some View {
        if vpn.profile.servers.isEmpty {
            Button {
                openWindow(id: "main")
                NSApp.activate(ignoringOtherApps: true)
            } label: {
                HStack(spacing: 10) {
                    Image(systemName: "plus.circle.fill").foregroundStyle(Theme.violet)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Добавь первый сервер").font(Theme.body(13, .bold))
                        Text("Окно → Серверы → вставь ссылку vless://…").font(Theme.body(11)).foregroundStyle(Theme.muted)
                    }
                    Spacer()
                }
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 14).strokeBorder(style: StrokeStyle(lineWidth: 1, dash: [5, 5])).foregroundStyle(Theme.violet.opacity(0.5)))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        } else {
            VStack(alignment: .leading, spacing: 6) {
                Text("Серверы").font(Theme.body(12, .bold)).foregroundStyle(Theme.muted)
                // Up to three rows inline; more scroll, so the menu always fits a 13" screen.
                let rows = ForEach(vpn.profile.servers) { s in
                    ServerRow(server: s, health: vpn.health[s.id], active: vpn.status == .on)
                }
                if vpn.profile.servers.count > 3 {
                    ScrollView { VStack(spacing: 6) { rows } }
                        .scrollIndicators(.automatic)
                        .frame(height: 3 * 48 + 2 * 6)
                } else {
                    rows
                }
            }
        }
    }

    private var defaultRoute: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text("Всё остальное идёт через").font(Theme.body(12, .bold)).foregroundStyle(Theme.muted)
            let options = vpn.profile.servers.map { ($0.id, $0.code, Color(hex: $0.colorHex)) } + [(Target.direct, "DIRECT", Theme.directGray)]
            if options.count <= 4 {
                RouteSegment(options: options, selected: vpn.profile.settings.defaultTarget) { vpn.setDefaultTarget($0) }
            } else {
                // Too many for a segmented control: a dropdown chip instead.
                HStack {
                    TargetMenu(selected: vpn.profile.settings.defaultTarget) { vpn.setDefaultTarget($0) }
                    Spacer()
                    Text(vpn.profile.server(vpn.profile.settings.defaultTarget)?.name ?? "напрямую")
                        .font(Theme.body(12)).foregroundStyle(Theme.muted).lineLimit(1)
                }
            }
        }
    }

    private var footer: some View {
        HStack(spacing: 10) {
            Button {
                openWindow(id: "main")
                NSApp.activate(ignoringOtherApps: true)
            } label: {
                Label("Маршруты", systemImage: "point.topleft.down.to.point.bottomright.curvepath")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(NovaButtonStyle())

            HStack(spacing: 8) {
                Text("Kill switch").font(Theme.body(12, .bold)).foregroundStyle(Theme.soft)
                    .lineLimit(1).fixedSize()
                Toggle("Kill switch", isOn: Binding(get: { vpn.profile.settings.killSwitch }, set: { vpn.setKillSwitch($0) }))
                    .toggleStyle(NovaToggleStyle())
                    .labelsHidden()
            }
            .padding(.horizontal, 10)
            .frame(height: 36)
            .overlay(RoundedRectangle(cornerRadius: 11).stroke(Color.white.opacity(0.1)))

            Button {
                vpn.quit()
            } label: {
                Image(systemName: "power")
            }
            .buttonStyle(NovaButtonStyle())
            .help("Выключить VPN и выйти")
            .accessibilityLabel(Text("Выйти"))
        }
    }
}

struct StatusPill: View {
    @EnvironmentObject var vpn: VPNController

    var body: some View {
        let (text, color): (String, Color) = {
            switch vpn.status {
            case .on: return vpn.hasDownServer ? ("Сбой", Theme.amber) : ("Активен", Theme.mint)
            case .connecting: return ("Старт", Theme.amber)
            case .error: return ("Ошибка", Theme.danger)
            default: return ("Выкл", Theme.muted)
            }
        }()
        HStack(spacing: 7) {
            PulseDot(color: color)
            Text(text).font(Theme.body(12, .bold))
        }
        .foregroundStyle(color)
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Capsule().fill(color.opacity(0.12)))
    }
}

/// The "nova" power button: rings, rotating halo and sparks when connected.
struct NovaOrb: View {
    enum OrbState { case off, connecting, on, warning, error }

    let state: OrbState
    let action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            ZStack {
                // Halo, rings, sparks and the connecting arc are Core Animation layers (GPU, no per-frame work).
                OrbEffects(state: state)
                    .frame(width: 156, height: 156)
                    .allowsHitTesting(false)
                core
            }
            .frame(width: 156, height: 156)
            .contentShape(Circle())
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .accessibilityLabel(Text(state == .off ? "Включить VPN" : "Выключить VPN"))
    }

    private var accent: Color {
        switch state {
        case .warning: return Theme.amber
        case .error: return Theme.danger
        default: return Theme.violet
        }
    }

    private var core: some View {
        let lit = state == .on || state == .warning || state == .connecting
        let colors: [Color] = {
            switch state {
            case .on: return [Color(hex: "#B4A9FF"), Color(hex: "#5B4BE6"), Color(hex: "#1F1766")]
            case .warning: return [Color(hex: "#FFE1A3"), Color(hex: "#D4891C"), Color(hex: "#4A2C05")]
            case .error: return [Color(hex: "#FF9AA4"), Color(hex: "#B8283A"), Color(hex: "#3A0A12")]
            case .connecting: return [Color(hex: "#6F64C9"), Color(hex: "#2E2777"), Color(hex: "#151235")]
            case .off: return [Color(hex: "#262B4A"), Color(hex: "#0E1020")]
            }
        }()
        return ZStack {
            Circle()
                .fill(RadialGradient(colors: colors, center: UnitPoint(x: 0.34, y: 0.26), startRadius: 2, endRadius: 90))
                .overlay(Circle().stroke(Color.white.opacity(lit ? 0.35 : 0.1)))
                .shadow(color: accent.opacity(lit ? 0.6 : 0), radius: 36)
            Image(systemName: "power")
                .font(.system(size: 34, weight: .semibold))
                .foregroundStyle(Theme.text)
        }
        .frame(width: 108, height: 108)
        .scaleEffect(hovering ? 1.04 : 1)
        .animation(.spring(response: 0.4, dampingFraction: 0.6), value: hovering)
        .animation(.easeInOut(duration: 0.6), value: state)
    }
}

/// Only this row re-renders on the per-second traffic updates.
struct SpeedsRow: View {
    @ObservedObject var stats: LiveStats
    let color: Color
    let active: Bool

    var body: some View {
        HStack(spacing: 10) {
            SpeedTile(title: "Загрузка", value: stats.down, history: stats.downHistory, color: color, active: active)
            SpeedTile(title: "Отдача", value: stats.up, history: stats.upHistory, color: Theme.mint.opacity(0.75), active: active)
        }
    }
}

struct SpeedTile: View {
    let title: String
    let value: Int
    let history: [Double]
    let color: Color
    let active: Bool

    var body: some View {
        let (num, unit) = formatSpeed(active ? value : 0)
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(Theme.body(12)).foregroundStyle(Theme.muted)
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(num).font(Theme.display(19, .bold)).monospacedDigit()
                Text(unit).font(Theme.body(11, .medium)).foregroundStyle(Theme.muted)
            }
            Sparkbars(values: history, color: color, active: active)
                .frame(height: 22)
                .padding(.top, 4)
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.white.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.white.opacity(0.07)))
    }
}

struct Sparkbars: View {
    let values: [Double]
    let color: Color
    let active: Bool

    var body: some View {
        let maxV = max(values.max() ?? 1, 1)
        HStack(alignment: .bottom, spacing: 2) {
            ForEach(Array(values.suffix(24).enumerated()), id: \.offset) { _, v in
                RoundedRectangle(cornerRadius: 1.5)
                    .fill(active ? color : Color.white.opacity(0.1))
                    .frame(maxWidth: .infinity)
                    .frame(height: active ? max(3, 22 * v / maxV) : 3)
            }
        }
        .frame(maxHeight: .infinity, alignment: .bottom)
    }
}

struct ServerRow: View {
    let server: Server
    let health: HealthMonitor.ServerHealth?
    let active: Bool

    var body: some View {
        let color = Color(hex: server.colorHex)
        HStack(spacing: 12) {
            Text(server.code)
                .font(Theme.display(12, .heavy))
                .frame(width: 34, height: 34)
                .background(RoundedRectangle(cornerRadius: 10).fill(color))
            VStack(alignment: .leading, spacing: 1) {
                Text(server.name).font(Theme.body(14, .bold)).lineLimit(1)
                Text(server.protocolName.uppercased()).font(Theme.body(11)).foregroundStyle(Theme.muted)
            }
            Spacer()
            if active {
                let down = health?.isDown == true
                PulseDot(color: down ? Theme.danger : Theme.mint)
                Text(down ? (health?.fallbackActive == true ? "напрямую" : "недоступен") : health?.delayMs.map { "\($0) мс" } ?? "…")
                    .font(Theme.mono(12))
                    .foregroundStyle(down ? Theme.danger : Theme.mint)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 7)
        .background(
            RoundedRectangle(cornerRadius: 14)
                .fill(LinearGradient(colors: [color.opacity(0.16), color.opacity(0.02)], startPoint: .leading, endPoint: .trailing))
        )
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(color.opacity(0.25)))
    }
}

/// Segmented control with a springy sliding indicator.
struct RouteSegment: View {
    let options: [(id: String, label: String, color: Color)]
    let selected: String
    let onSelect: (String) -> Void

    var body: some View {
        let index = options.firstIndex { $0.id == selected } ?? 0
        let color = options.indices.contains(index) ? options[index].color : Theme.directGray
        GeometryReader { geo in
            let w = geo.size.width / CGFloat(max(options.count, 1))
            ZStack(alignment: .leading) {
                RoundedRectangle(cornerRadius: 10)
                    .fill(color)
                    .shadow(color: color.opacity(0.5), radius: 10, y: 4)
                    .frame(width: w, height: 36)
                    .offset(x: w * CGFloat(index))
                    .animation(.spring(response: 0.42, dampingFraction: 0.68), value: index)
                    .animation(.easeOut(duration: 0.3), value: selected)
                HStack(spacing: 0) {
                    ForEach(options, id: \.id) { opt in
                        Button {
                            onSelect(opt.id)
                        } label: {
                            Text(opt.label)
                                .font(Theme.body(13, .bold))
                                .lineLimit(1)
                                .minimumScaleFactor(0.7)
                                .foregroundStyle(opt.id == selected ? .white : Theme.muted)
                                .frame(width: w, height: 36)
                                .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .frame(height: 36)
        .padding(4)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.white.opacity(0.05)))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.white.opacity(0.07)))
    }
}

struct SetupCard: View {
    @EnvironmentObject var vpn: VPNController
    var update = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(update ? "Приложение обновилось — обнови фоновую службу." : "VPN Nova запускает ядро sing-box как фоновую службу. Для установки macOS попросит пароль администратора — один раз.")
                .font(Theme.body(13))
                .foregroundStyle(Theme.soft)
                .fixedSize(horizontal: false, vertical: true)
            Button(update ? "Обновить службу" : "Установить службу") { vpn.install() }
                .buttonStyle(NovaButtonStyle(prominent: true))
        }
        .padding(14)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.white.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Theme.violet.opacity(0.3)))
    }
}
