import AppKit
import NovaCore
import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var vpn: VPNController
    @State private var confirmUninstall = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                PageHeader(title: "Настройки", subtitle: "Как ведёт себя VPN Nova")
                KillSwitchCard(on: vpn.profile.settings.killSwitch) { vpn.setKillSwitch($0) }

                VStack(spacing: 0) {
                    option("Запускать при входе в систему", "Приложение откроется само после перезагрузки",
                           vpn.profile.settings.launchAtLogin) { vpn.setLaunchAtLogin($0) }
                    option("Подключаться при запуске", "VPN включится сразу, как откроется приложение",
                           vpn.profile.settings.connectOnLaunch) { v in vpn.update(restart: false) { $0.settings.connectOnLaunch = v } }
                    option("Скорость в строке меню", "Рядом с иконкой: ↓ 4,2 МБ/с",
                           vpn.profile.settings.showSpeedInMenuBar) { v in vpn.update(restart: false) { $0.settings.showSpeedInMenuBar = v } }
                    option("Обновлять наборы правил", "Раз в неделю: YouTube, Китай и другие",
                           vpn.profile.settings.autoUpdateRuleSets) { v in vpn.update { $0.settings.autoUpdateRuleSets = v } }
                    option("Подробный лог", "Записывать каждое соединение: сайты и приложения. Для отладки, потом лучше выключить",
                           vpn.profile.settings.verboseLog, last: true) { v in vpn.update { $0.settings.verboseLog = v } }
                }
                .padding(.horizontal, 18)
                .background(RoundedRectangle(cornerRadius: 18).fill(Theme.panel))
                .overlay(RoundedRectangle(cornerRadius: 18).stroke(Color.white.opacity(0.07)))

                Card(padding: 18, radius: 18) {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("DNS").font(Theme.body(14, .bold))
                        Text("Сайты, которые идут через сервер, резолвит сам сервер: приложение получает служебный адрес, а на сервер уходит имя сайта. Подмена DNS у провайдера не мешает, падение одного сервера не ломает DNS для остальных. Локальные имена и сайты из правил DIRECT резолвит системный DNS.")
                            .font(Theme.body(12)).foregroundStyle(Theme.muted).fixedSize(horizontal: false, vertical: true)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }

                coreCard

                Text("VPN Nova \(VPNController.appVersion) · служба \(VPNController.serviceVersion) · ядро \(Self.coreVersion)")
                    .font(Theme.mono(11))
                    .foregroundStyle(Theme.muted)
                    .textSelection(.enabled)
            }
            .padding(.horizontal, 36)
            .padding(.vertical, 28)
            .frame(maxWidth: 820, alignment: .leading)
        }
        .scrollIndicators(.never)
        .confirmationDialog("Удалить фоновую службу?", isPresented: $confirmUninstall) {
            Button("Удалить", role: .destructive) { vpn.uninstall() }
        } message: {
            Text("Ядро, правило sudo и логи будут удалены. Профиль с серверами и правилами останется.")
        }
    }

    private func option(_ title: String, _ hint: String, _ value: Bool, last: Bool = false, _ set: @escaping (Bool) -> Void) -> some View {
        HStack(spacing: 14) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(Theme.body(14, .bold))
                Text(hint).font(Theme.body(12)).foregroundStyle(Theme.muted)
            }
            Spacer()
            Toggle(title, isOn: Binding(get: { value }, set: set)).toggleStyle(NovaToggleStyle()).labelsHidden()
        }
        .padding(.vertical, 14)
        .overlay(alignment: .bottom) { if !last { Rectangle().fill(Theme.line).frame(height: 1) } }
    }

    private var coreCard: some View {
        let installed = PrivilegedCore.isInstalled
        let running = vpn.status == .on
        return Card(padding: 18, radius: 18) {
            HStack(spacing: 14) {
                PulseDot(color: running ? Theme.mint : installed ? Theme.muted : Theme.amber)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Ядро sing-box \(Self.coreVersion)").font(Theme.body(14, .bold))
                    Text(installed ? (running ? "работает как служба · TUN" : "служба установлена, остановлена") : "служба не установлена")
                        .font(Theme.body(12)).foregroundStyle(Theme.muted)
                }
                Spacer()
                if installed {
                    Button { vpn.install() } label: { Label("Переустановить", systemImage: "arrow.triangle.2.circlepath") }
                        .buttonStyle(NovaButtonStyle())
                    Button { confirmUninstall = true } label: { Label("Удалить", systemImage: "trash") }
                        .buttonStyle(NovaButtonStyle())
                } else {
                    Button("Установить") { vpn.install() }.buttonStyle(NovaButtonStyle(prominent: true))
                }
                Button {
                    NSWorkspace.shared.activateFileViewerSelecting([ProfileStore.profileURL])
                } label: { Label("Профиль", systemImage: "folder") }
                    .buttonStyle(NovaButtonStyle())
                    .help("Показать profile.json в Finder")
            }
        }
    }

    static let coreVersion = Bundle.main.object(forInfoDictionaryKey: "NovaSingBoxVersion") as? String ?? ""
}

struct KillSwitchCard: View {
    let on: Bool
    let set: (Bool) -> Void

    var body: some View {
        HStack(spacing: 18) {
            ZStack {
                RoundedRectangle(cornerRadius: 24).fill(on ? Color(hex: "#0E3B2E") : Color(hex: "#1A1D30"))
                SweepingLight(axis: .vertical, color: Theme.mint.opacity(0.45), thickness: 22, period: 2.2, active: on)
                    .clipShape(RoundedRectangle(cornerRadius: 24))
                    .allowsHitTesting(false)
                Image(systemName: on ? "checkmark.shield" : "shield")
                    .font(.system(size: 36, weight: .medium))
                    .foregroundStyle(on ? Theme.mint : Color(hex: "#6B7092"))
            }
            .frame(width: 84, height: 84)
            .shadow(color: on ? Theme.mint.opacity(0.35) : .clear, radius: 20)

            VStack(alignment: .leading, spacing: 4) {
                Text("Kill switch").font(Theme.display(18, .bold))
                Text(on ? "Если сервер упал, его трафик блокируется. Ничего не утечёт напрямую."
                        : "Если сервер упал, его трафик временно пойдёт напрямую.")
                    .font(Theme.body(13)).foregroundStyle(Theme.soft)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Toggle("Kill switch", isOn: Binding(get: { on }, set: set)).toggleStyle(NovaToggleStyle()).labelsHidden()
        }
        .padding(22)
        .background(
            RoundedRectangle(cornerRadius: 22)
                .fill(on ? AnyShapeStyle(LinearGradient(colors: [Theme.mint.opacity(0.16), Theme.mint.opacity(0.02)], startPoint: .topLeading, endPoint: .bottomTrailing))
                         : AnyShapeStyle(Theme.panel))
        )
        .overlay(RoundedRectangle(cornerRadius: 22).stroke(on ? Theme.mint.opacity(0.35) : Color.white.opacity(0.08)))
        .animation(.easeInOut(duration: 0.5), value: on)
    }
}
