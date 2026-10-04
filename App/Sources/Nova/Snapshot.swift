import AppKit
import NovaCore
import SwiftUI

/// Debug renders with demo data, used to check the UI without a person at the screen.
/// `Nova --snapshot <dir>` opens real windows for every screen, captures them to PNG and exits.
@MainActor
enum Snapshot {
    static var requestedDir: URL? {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--snapshot"), i + 1 < args.count else { return nil }
        return URL(fileURLWithPath: args[i + 1])
    }

    /// `Nova --demo popover|routes|rules|servers|settings|logs` keeps one live demo window open
    /// at the top-left of the screen, for recording animations.
    static var demoController: VPNController?
    static var demoWindow: NSWindow?

    static func runDemoIfRequested() {
        let args = CommandLine.arguments
        guard let i = args.firstIndex(of: "--demo") else { return }
        let what = i + 1 < args.count ? args[i + 1] : "popover"
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            let vpn = VPNController(demo: true)
            demoController = vpn
            let view: AnyView
            let size: CGSize
            if what == "popover" {
                view = AnyView(PopoverView().environmentObject(vpn))
                size = CGSize(width: 380, height: 700)
            } else {
                view = AnyView(MainWindow(section: Section(rawValue: what) ?? .routes).environmentObject(vpn))
                size = CGSize(width: 1100, height: 690)
            }
            let host = NSHostingView(rootView: view)
            let window = NSWindow(contentRect: CGRect(origin: .zero, size: size),
                                  styleMask: what == "popover" ? [.borderless] : [.titled, .closable, .resizable, .fullSizeContentView],
                                  backing: .buffered, defer: false)
            window.titlebarAppearsTransparent = true
            window.contentView = host
            if what == "popover" { window.setContentSize(host.fittingSize) }
            let screen = NSScreen.main!.visibleFrame
            window.setFrameTopLeftPoint(NSPoint(x: screen.minX, y: screen.maxY))
            window.level = .floating
            window.orderFrontRegardless()
            demoWindow = window
            if args.contains("--cycle-sections") {
                Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { _ in
                    NotificationCenter.default.post(name: .novaDemoNextSection, object: nil)
                }
            }
            if args.contains("--cycle") {
                Timer.scheduledTimer(withTimeInterval: 3.5, repeats: true) { _ in
                    Task { @MainActor in vpn.toggle() }
                }
            }
            print("demo window frame:", window.frame, "screen:", NSScreen.main!.frame)
        }
    }

    static func runIfRequested() {
        guard let dir = requestedDir else { return }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
            Task { @MainActor in
                await shoot(dir)
                exit(0)
            }
        }
    }

    private static func shoot(_ dir: URL) async {
        let vpn = VPNController(demo: true)
        await capture(PopoverView().environmentObject(vpn), size: CGSize(width: 380, height: 760), name: "popover", dir: dir)
        for s in Section.allCases {
            await capture(MainWindow(section: s).environmentObject(vpn), size: CGSize(width: 1100, height: 690), name: "window-\(s.rawValue)", dir: dir)
            await capture(MainWindow(section: s).environmentObject(vpn), size: CGSize(width: 980, height: 600), name: "min-\(s.rawValue)", dir: dir)
        }
        let off = VPNController(demo: true)
        off.demoSetStatus(.notInstalled)
        await capture(PopoverView().environmentObject(off), size: CGSize(width: 380, height: 640), name: "popover-setup", dir: dir)

        // Edge states.
        func variant(_ name: String, _ setup: (VPNController) -> Void, window: Section? = nil) async {
            let v = VPNController(demo: true)
            setup(v)
            if let window {
                await capture(MainWindow(section: window).environmentObject(v), size: CGSize(width: 1100, height: 690), name: name, dir: dir)
            } else {
                await capture(PopoverView().environmentObject(v), size: CGSize(width: 360, height: 400), name: name, dir: dir)
            }
        }
        await variant("st-needs-update") { $0.demoSet(status: .needsUpdate) }
        await variant("st-error") { $0.demoSet(status: .error("Ядро не запустилось. Загляни в логи."), lastError: "novactl: конфиг должен принадлежать пользователю — очень длинное сообщение об ошибке, которое должно переноситься, а не обрезаться") }
        await variant("st-warning") { $0.demoSet(health: ["de": .init(delayMs: nil, failures: 3, fallbackActive: false), "ru": .init(delayMs: 18)], otherVPNs: ["Corp VPN", "Other VPN"]) }
        await variant("st-fallback") { $0.demoSet(health: ["de": .init(delayMs: nil, failures: 3, fallbackActive: true), "ru": .init(delayMs: 18)]) }
        await variant("st-connecting") { $0.demoSet(status: .connecting) }
        await variant("st-off-empty") { $0.demoSet(status: .off, profile: Profile.starter()) }
        var many = Profile()
        many.servers = (0..<6).map { i in
            Server(id: "s\(i)", name: ["Германия — Франкфурт-на-Майне, очень длинное имя", "Россия", "Нидерланды", "Финляндия", "США", "Япония"][i],
                   code: ["DE", "RU", "NL", "FI", "USA", "JPN"][i], colorHex: Server.palette[i],
                   outbound: i == 3 ? ["type": "trojan", "server": "203.0.113.3", "server_port": 443, "tls": ["enabled": true, "insecure": true]] : ["type": "vless", "server": .string("203.0.113.\(i)"), "server_port": 443])
        }
        many.settings.defaultTarget = "s5"
        for i in 0..<40 {
            many.domainRules.append(DomainRule(kind: .domain, value: "very-long-subdomain-\(i).example-domain-name.com", target: "s\(i % 6)"))
        }
        many.appRules = [AppRule(name: "Очень длинное название приложения, которое не помещается", path: "/Applications/Очень длинное название приложения, которое не помещается.app", target: "s1")]
        await variant("st-many-popover") { $0.demoSet(profile: many) }
        await variant("st-many-rules", { $0.demoSet(profile: many) }, window: .rules)
        await variant("st-many-servers", { $0.demoSet(profile: many) }, window: .servers)
        await variant("st-many-routes", { $0.demoSet(profile: many) }, window: .routes)
        await variant("st-routes-off", { $0.demoSet(status: .off, otherVPNs: ["Corp VPN"], connections: []) }, window: .routes)
        await variant("st-routes-quiet", { $0.demoSet(connections: []) }, window: .routes)

        // Sheets.
        let sheetVPN = VPNController(demo: true)
        await capture(AddDomainSheet().environmentObject(sheetVPN), size: CGSize(width: 560, height: 420), name: "sheet-domain", dir: dir)
        await capture(AppPickerSheet().environmentObject(sheetVPN), size: CGSize(width: 600, height: 640), name: "sheet-apps", dir: dir)
        await capture(ServerEditor(original: nil).environmentObject(sheetVPN), size: CGSize(width: 620, height: 640), name: "sheet-server-new", dir: dir)
        await capture(ServerEditor(original: sheetVPN.profile.servers.first).environmentObject(sheetVPN), size: CGSize(width: 620, height: 640), name: "sheet-server-edit", dir: dir)
    }

    private static func capture<V: View>(_ view: V, size: CGSize, name: String, dir: URL) async {
        let host = NSHostingView(rootView: view.frame(width: size.width).frame(minHeight: size.height, alignment: .top))
        host.frame = CGRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: host.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = host
        window.backgroundColor = .black
        window.setFrameOrigin(NSPoint(x: 40, y: 40))
        window.orderFrontRegardless()
        try? await Task.sleep(nanoseconds: 1_400_000_000)
        let fit = host.fittingSize
        if fit.height > size.height + 1 {
            window.setContentSize(CGSize(width: size.width, height: fit.height))
            host.frame.size = CGSize(width: size.width, height: fit.height)
            try? await Task.sleep(nanoseconds: 600_000_000)
        }
        guard let rep = host.bitmapImageRepForCachingDisplay(in: host.bounds) else { return }
        host.cacheDisplay(in: host.bounds, to: rep)
        try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent("\(name).png"))
        print("✓ \(name).png \(Int(host.frame.width))×\(Int(host.frame.height)) (fits \(Int(fit.width))×\(Int(fit.height)))")
        window.orderOut(nil)
    }
}

/// `Nova --cpu-test` clicks its own menu bar icon (open, then close) and prints the CPU
/// the app uses in each phase. Catches animations that keep running in a closed popover.
@MainActor
enum CPUTest {
    static func runIfRequested() {
        guard CommandLine.arguments.contains("--cpu-test") else { return }
        Task { @MainActor in
            try? await Task.sleep(nanoseconds: 3_000_000_000)
            report("закрыто, до открытия", await measure())
            clickStatusItem()
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            report("меню открыто", await measure())
            clickStatusItem()
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            report("меню закрыто снова", await measure())
            exit(0)
        }
    }

    static func clickStatusItem() {
        for w in NSApp.windows where w.className.contains("StatusBar") {
            if let button = findButton(w.contentView) {
                button.performClick(nil)
                return
            }
        }
        print("иконка в строке меню не найдена")
    }

    static func findButton(_ v: NSView?) -> NSButton? {
        guard let v else { return nil }
        if let b = v as? NSButton { return b }
        for s in v.subviews { if let b = findButton(s) { return b } }
        return nil
    }

    static func cpuSeconds() -> Double {
        var u = rusage()
        getrusage(RUSAGE_SELF, &u)
        return Double(u.ru_utime.tv_sec + u.ru_stime.tv_sec) + Double(u.ru_utime.tv_usec + u.ru_stime.tv_usec) / 1e6
    }

    static func measure(seconds: Double = 4) async -> Double {
        let start = cpuSeconds()
        try? await Task.sleep(nanoseconds: UInt64(seconds * 1e9))
        return (cpuSeconds() - start) / seconds * 100
    }

    static func report(_ phase: String, _ pct: Double) {
        print(String(format: "CPU %@: %.1f%%", phase, pct))
        fflush(stdout)
    }
}
