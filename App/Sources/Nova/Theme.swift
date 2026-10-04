import AppKit
import CoreText
import NovaCore
import SwiftUI

enum Theme {
    static let bg = Color(hex: "#07080F")
    static let bgDeep = Color(hex: "#05060C")
    static let panel = Color(hex: "#0F1120")
    static let panel2 = Color(hex: "#161A2E")
    static let line = Color.white.opacity(0.08)
    static let text = Color(hex: "#EEF0FF")
    static let muted = Color(hex: "#8A90B2")
    static let soft = Color(hex: "#B8BDD9")
    static let violet = Color(hex: "#8B7CFF")
    static let violetDeep = Color(hex: "#6A5AF0")
    static let coral = Color(hex: "#FF7A59")
    static let mint = Color(hex: "#3BE8B0")
    static let amber = Color(hex: "#FFC14D")
    static let danger = Color(hex: "#FF5C6C")
    static let directGray = Color(hex: "#3A3F5C")

    static func display(_ size: CGFloat, _ weight: Font.Weight = .bold) -> Font {
        Fonts.available ? .custom("Unbounded", size: size).weight(weight) : .system(size: size, weight: weight, design: .rounded)
    }

    static func body(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        Fonts.available ? .custom("Manrope", size: size).weight(weight) : .system(size: size, weight: weight)
    }

    static func mono(_ size: CGFloat) -> Font { .system(size: size, weight: .medium, design: .monospaced) }

    static func color(for target: TargetID, in profile: Profile) -> Color {
        if let s = profile.server(target) { return Color(hex: s.colorHex) }
        return directGray
    }

    static func label(for target: TargetID, in profile: Profile) -> String {
        profile.server(target)?.code ?? "DIRECT"
    }
}

enum Fonts {
    private(set) static var available = false

    static func register() {
        guard let dir = Bundle.main.resourceURL?.appendingPathComponent("Fonts"),
              let files = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return }
        var ok = 0
        for f in files where f.pathExtension == "ttf" {
            if CTFontManagerRegisterFontsForURL(f as CFURL, .process, nil) { ok += 1 }
        }
        available = ok >= 2
    }
}

extension Color {
    init(hex: String) {
        var s = hex.trimmingCharacters(in: .whitespaces)
        if s.hasPrefix("#") { s.removeFirst() }
        let v = UInt64(s, radix: 16) ?? 0x888888
        self.init(.sRGB,
                  red: Double((v >> 16) & 0xFF) / 255,
                  green: Double((v >> 8) & 0xFF) / 255,
                  blue: Double(v & 0xFF) / 255)
    }
}

/// Standard dark card.
struct Card<Content: View>: View {
    var padding: CGFloat = 16
    var radius: CGFloat = 16
    @ViewBuilder var content: Content

    var body: some View {
        content
            .padding(padding)
            .background(RoundedRectangle(cornerRadius: radius).fill(Theme.panel))
            .overlay(RoundedRectangle(cornerRadius: radius).stroke(Theme.line))
    }
}

/// Server code chip ("DE", "RU", "DIRECT").
struct TargetChip: View {
    let label: String
    let color: Color
    var showChevron = false

    var body: some View {
        HStack(spacing: 5) {
            Text(label).font(Theme.display(11, .bold))
            if showChevron { Image(systemName: "chevron.down").font(.system(size: 9, weight: .heavy)) }
        }
        .foregroundStyle(.white)
        .padding(.horizontal, 10)
        .frame(minWidth: 58, minHeight: 30)
        .background(Capsule().fill(color))
        .shadow(color: color.opacity(0.45), radius: 9, y: 3)
    }
}

/// Pulsing status dot (Core Animation; no per-frame work on the main thread).
struct PulseDot: View {
    var color: Color = Theme.mint

    var body: some View {
        PulseDotLayer(color: color).frame(width: 8, height: 8)
    }
}

struct NovaToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            ZStack(alignment: configuration.isOn ? .trailing : .leading) {
                Capsule().fill(configuration.isOn ? Theme.mint : Color(hex: "#2A2E48"))
                    .frame(width: 46, height: 26)
                Circle().fill(.white).frame(width: 20, height: 20).padding(3)
            }
            .animation(.spring(response: 0.35, dampingFraction: 0.6), value: configuration.isOn)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text("Переключатель"))
        .accessibilityValue(Text(configuration.isOn ? "вкл" : "выкл"))
    }
}

struct NovaButtonStyle: ButtonStyle {
    var prominent = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(Theme.body(13, .bold))
            .foregroundStyle(Theme.text)
            .padding(.horizontal, 14)
            .frame(minHeight: 36)
            .background(
                RoundedRectangle(cornerRadius: 11)
                    .fill(prominent ? Theme.violetDeep : Color.white.opacity(configuration.isPressed ? 0.1 : 0.05))
            )
            .overlay(RoundedRectangle(cornerRadius: 11).stroke(prominent ? Theme.violet : Color.white.opacity(0.12)))
            .shadow(color: prominent ? Theme.violetDeep.opacity(0.45) : .clear, radius: 12, y: 5)
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .animation(.easeOut(duration: 0.15), value: configuration.isPressed)
    }
}

extension View {
    func novaTextField() -> some View {
        self.textFieldStyle(.plain)
            .font(Theme.body(14, .medium))
            .padding(.horizontal, 12)
            .frame(height: 38)
            .background(RoundedRectangle(cornerRadius: 10).fill(Color.white.opacity(0.05)))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(Color.white.opacity(0.1)))
    }
}

func formatSpeed(_ bytesPerSec: Int) -> (String, String) {
    let b = Double(bytesPerSec)
    if b >= 1_000_000 { return (String(format: "%.1f", b / 1_000_000).replacingOccurrences(of: ".", with: ","), "МБ/с") }
    if b >= 1_000 { return (String(Int(b / 1_000)), "КБ/с") }
    return (String(bytesPerSec), "Б/с")
}

/// Russian plural: plural(1, "соединение", "соединения", "соединений").
func plural(_ n: Int, _ one: String, _ few: String, _ many: String) -> String {
    let m10 = n % 10, m100 = n % 100
    if m10 == 1 && m100 != 11 { return "\(n) \(one)" }
    if (2...4).contains(m10) && !(12...14).contains(m100) { return "\(n) \(few)" }
    return "\(n) \(many)"
}

// MARK: - Pausing animations when nobody can see them

private struct NovaAnimateKey: EnvironmentKey {
    static let defaultValue = true
}

extension EnvironmentValues {
    /// False while the hosting window is closed or fully covered: continuous animations pause.
    var novaAnimate: Bool {
        get { self[NovaAnimateKey.self] }
        set { self[NovaAnimateKey.self] = newValue }
    }
}

/// Reports whether the hosting window is actually visible on screen.
private struct WindowVisibilityReader: NSViewRepresentable {
    @Binding var visible: Bool

    final class Probe: NSView {
        var onChange: ((Bool) -> Void)?
        private var tokens: [NSObjectProtocol] = []

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            tokens.forEach(NotificationCenter.default.removeObserver)
            tokens = []
            guard let window else {
                onChange?(false)
                return
            }
            let names: [Notification.Name] = [NSWindow.didChangeOcclusionStateNotification, NSWindow.willCloseNotification,
                                              NSWindow.didMiniaturizeNotification, NSWindow.didDeminiaturizeNotification]
            for name in names {
                tokens.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] n in
                    self?.report(closing: n.name == NSWindow.willCloseNotification)
                })
            }
            report(closing: false)
        }

        private func report(closing: Bool) {
            guard let w = window else { return }
            onChange?(!closing && w.isVisible && w.occlusionState.contains(.visible))
        }

        deinit { tokens.forEach(NotificationCenter.default.removeObserver) }
    }

    func makeNSView(context: Context) -> Probe {
        let p = Probe()
        p.onChange = { v in DispatchQueue.main.async { if visible != v { visible = v } } }
        return p
    }

    func updateNSView(_ nsView: Probe, context: Context) {}
}

private struct TrackVisibility: ViewModifier {
    @State private var visible = true

    func body(content: Content) -> some View {
        content
            .background(WindowVisibilityReader(visible: $visible).frame(width: 0, height: 0))
            .environment(\.novaAnimate, visible)
    }
}

extension View {
    /// Pauses `novaAnimate`-aware animations while this window isn't visible.
    func pausesAnimationsWhenHidden() -> some View { modifier(TrackVisibility()) }
}
