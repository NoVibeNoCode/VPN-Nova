import AppKit
import NovaCore
import SwiftUI

@main
struct NovaApp: App {
    @StateObject private var vpn: VPNController

    init() {
        Fonts.register()
        Snapshot.runIfRequested()
        Snapshot.runDemoIfRequested()
        CPUTest.runIfRequested()
        _vpn = StateObject(wrappedValue: VPNController())
    }

    var body: some Scene {
        MenuBarExtra {
            PopoverView().environmentObject(vpn)
        } label: {
            MenuBarLabel(stats: vpn.stats).environmentObject(vpn)
        }
        .menuBarExtraStyle(.window)

        Window("VPN Nova", id: "main") {
            MainWindow().environmentObject(vpn)
        }
        .windowStyle(.hiddenTitleBar)
        .defaultSize(width: 1120, height: 700)
        .windowResizability(.contentMinSize)
    }
}

struct MenuBarLabel: View {
    @EnvironmentObject var vpn: VPNController
    @ObservedObject var stats: LiveStats

    var body: some View {
        HStack(spacing: 4) {
            Image(systemName: icon)
            if vpn.profile.settings.showSpeedInMenuBar, vpn.status == .on {
                let (v, u) = formatSpeed(stats.down)
                Text("↓ \(v) \(u)").monospacedDigit()
            }
        }
    }

    private var icon: String {
        switch vpn.status {
        case .on: return vpn.hasDownServer ? "exclamationmark.shield" : "checkmark.shield.fill"
        case .connecting: return "shield.lefthalf.filled"
        case .error: return "xmark.shield"
        default: return "shield"
        }
    }
}
