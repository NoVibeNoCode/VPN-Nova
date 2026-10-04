import AppKit
import QuartzCore
import SwiftUI

// Continuous animations as Core Animation layers: the window server animates them on the GPU,
// so the main thread does no work per frame (SwiftUI TimelineView/blur redrew on the CPU at 60 fps).

/// Layer-hosting view with a flipped (y-down) coordinate space, like SwiftUI.
class CAHostView: NSView {
    let root = CALayer()

    override init(frame: NSRect) {
        super.init(frame: frame)
        root.isGeometryFlipped = true
        layer = root
        wantsLayer = true
        layerUsesCoreImageFilters = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    override var isFlipped: Bool { true }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layoutLayers()
        CATransaction.commit()
    }

    /// Animations can be dropped while a window is closed; subclasses re-add them here.
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        if window != nil { restartAnimations() }
    }

    func layoutLayers() {}
    func restartAnimations() {}
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

extension CAAnimation {
    static func forever<T: CAAnimation>(_ a: T, duration: CFTimeInterval, offset: CFTimeInterval = 0) -> T {
        a.duration = duration
        // Children of a group get the group's duration explicitly (their default is 0.25 s).
        (a as? CAAnimationGroup)?.animations?.forEach { $0.duration = duration }
        a.repeatCount = .infinity
        a.timeOffset = offset
        a.isRemovedOnCompletion = false
        return a
    }
}

func cgColor(_ c: Color, _ alpha: CGFloat = 1) -> CGColor {
    NSColor(c).withAlphaComponent(alpha).cgColor
}

// MARK: - Nova orb effects (halo, rings, sparks, connecting arc)

struct OrbEffects: NSViewRepresentable {
    let state: NovaOrb.OrbState

    func makeNSView(context: Context) -> OrbEffectsView { OrbEffectsView() }
    func updateNSView(_ view: OrbEffectsView, context: Context) { view.apply(state) }
}

final class OrbEffectsView: CAHostView {
    private let haloBox = CALayer()
    private let halo = CAGradientLayer()
    private let rings = (0..<3).map { _ in CAShapeLayer() }
    private let sparks = (0..<6).map { _ in CALayer() }
    private let arc = CAShapeLayer()
    private var state: NovaOrb.OrbState?

    private static let sparkSpots: [(CGFloat, CGFloat, CFTimeInterval)] =
        [(-63, -44, 0), (70, -26, 0.4), (-44, 63, 0.9), (51, 56, 1.3), (-5, -74, 1.8), (-74, 10, 2.1)]

    override init(frame: NSRect) {
        super.init(frame: frame)
        halo.type = .conic
        halo.startPoint = CGPoint(x: 0.5, y: 0.5)
        halo.endPoint = CGPoint(x: 0.5, y: 0)
        halo.masksToBounds = true
        haloBox.addSublayer(halo)
        haloBox.filters = [CIFilter(name: "CIGaussianBlur", parameters: [kCIInputRadiusKey: 16])!]
        haloBox.opacity = 0
        root.addSublayer(haloBox)
        for r in rings {
            r.fillColor = nil
            r.lineWidth = 1.5
            r.opacity = 0
            root.addSublayer(r)
        }
        for s in sparks {
            s.backgroundColor = NSColor.white.cgColor
            s.cornerRadius = 2
            s.opacity = 0
            root.addSublayer(s)
        }
        arc.fillColor = nil
        arc.lineWidth = 3
        arc.lineCap = .round
        arc.strokeEnd = 0.28
        arc.strokeColor = cgColor(Theme.violet)
        arc.isHidden = true
        root.addSublayer(arc)
    }

    override func layoutLayers() {
        let c = CGPoint(x: bounds.midX, y: bounds.midY)
        haloBox.bounds = CGRect(x: 0, y: 0, width: 190, height: 190)
        haloBox.position = c
        halo.bounds = CGRect(x: 0, y: 0, width: 126, height: 126)
        halo.cornerRadius = 63
        halo.position = CGPoint(x: 95, y: 95)
        let ringRect = CGRect(x: 0, y: 0, width: 156, height: 156)
        for r in rings {
            r.bounds = ringRect
            r.position = c
            r.path = CGPath(ellipseIn: ringRect, transform: nil)
        }
        for (s, spot) in zip(sparks, Self.sparkSpots) {
            s.bounds = CGRect(x: 0, y: 0, width: 4, height: 4)
            s.position = CGPoint(x: c.x + spot.0, y: c.y + spot.1)
        }
        let arcRect = CGRect(x: 0, y: 0, width: 128, height: 128)
        arc.bounds = arcRect
        arc.position = c
        arc.path = CGPath(ellipseIn: arcRect, transform: nil)
    }

    override func restartAnimations() {
        if let state { configure(state, animated: false, force: true) }
    }

    func apply(_ s: NovaOrb.OrbState) {
        guard s != state else { return }
        let first = state == nil
        state = s
        configure(s, animated: !first, force: true)
    }

    private func configure(_ s: NovaOrb.OrbState, animated: Bool, force: Bool) {
        let lit = s == .on || s == .warning
        let accent: Color = s == .warning ? Theme.amber : s == .error ? Theme.danger : Theme.violet

        CATransaction.begin()
        CATransaction.setAnimationDuration(animated ? 0.7 : 0)
        haloBox.opacity = {
            switch s {
            case .on, .warning: return 0.85
            case .connecting: return 0.4
            case .error: return 0.35
            case .off: return 0
            }
        }()
        halo.colors = (s == .error ? [Theme.danger, Theme.coral, Theme.danger] : [Theme.violet, Theme.coral, Theme.mint, Theme.violet]).map { cgColor($0) }
        for r in rings { r.strokeColor = cgColor(accent, 0.6) }
        CATransaction.commit()

        // Halo spin: slow when on, fast while connecting, none when off.
        halo.removeAnimation(forKey: "spin")
        if s != .off {
            let spin = CABasicAnimation(keyPath: "transform.rotation.z")
            spin.fromValue = 0
            spin.toValue = 2 * Double.pi
            halo.add(.forever(spin, duration: s == .connecting ? 1.4 : 7.2), forKey: "spin")
        }

        for (i, r) in rings.enumerated() {
            r.removeAnimation(forKey: "ripple")
            guard lit else { r.opacity = 0; continue }
            let scale = CABasicAnimation(keyPath: "transform.scale")
            scale.fromValue = 0.72
            scale.toValue = 1.32
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = 0.9
            fade.toValue = 0
            let g = CAAnimationGroup()
            g.animations = [scale, fade]
            g.timingFunction = CAMediaTimingFunction(controlPoints: 0.15, 0.6, 0.3, 1)
            r.opacity = 0
            r.add(.forever(g, duration: 3.6, offset: Double(i) * 1.2), forKey: "ripple")
        }

        for (s2, spot) in zip(sparks, Self.sparkSpots) {
            s2.removeAnimation(forKey: "twinkle")
            guard lit else { s2.opacity = 0; continue }
            let o = CAKeyframeAnimation(keyPath: "opacity")
            o.values = [0, 1, 0]
            let sc = CAKeyframeAnimation(keyPath: "transform.scale")
            sc.values = [0.4, 1.2, 0.4]
            let g = CAAnimationGroup()
            g.animations = [o, sc]
            g.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            s2.opacity = 0
            s2.add(.forever(g, duration: 2.4, offset: spot.2), forKey: "twinkle")
        }

        arc.removeAnimation(forKey: "spin")
        arc.isHidden = s != .connecting
        if s == .connecting {
            let spin = CABasicAnimation(keyPath: "transform.rotation.z")
            spin.fromValue = 0
            spin.toValue = 2 * Double.pi
            arc.add(.forever(spin, duration: 0.9), forKey: "spin")
        }
    }
}

// MARK: - Pulsing status dot

struct PulseDotLayer: NSViewRepresentable {
    let color: Color

    func makeNSView(context: Context) -> PulseDotView { PulseDotView() }
    func updateNSView(_ view: PulseDotView, context: Context) { view.setColor(cgColor(color)) }
}

final class PulseDotView: CAHostView {
    private let dot = CALayer()
    private let ring = CAShapeLayer()

    override init(frame: NSRect) {
        super.init(frame: frame)
        root.addSublayer(ring)
        root.addSublayer(dot)
        ring.fillColor = nil
        ring.lineWidth = 2
    }

    func setColor(_ c: CGColor) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        dot.backgroundColor = c
        ring.strokeColor = c.copy(alpha: 0.6)
        CATransaction.commit()
    }

    override func layoutLayers() {
        let r = CGRect(x: 0, y: 0, width: 8, height: 8)
        dot.bounds = r
        dot.cornerRadius = 4
        dot.position = CGPoint(x: bounds.midX, y: bounds.midY)
        ring.bounds = r
        ring.path = CGPath(ellipseIn: r.insetBy(dx: 1, dy: 1), transform: nil)
        ring.position = dot.position
    }

    override func restartAnimations() {
        ring.removeAllAnimations()
        let scale = CABasicAnimation(keyPath: "transform.scale")
        scale.fromValue = 1
        scale.toValue = 2.2
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = 1
        fade.toValue = 0
        let g = CAAnimationGroup()
        g.animations = [scale, fade]
        g.timingFunction = CAMediaTimingFunction(name: .easeOut)
        ring.opacity = 0
        ring.add(.forever(g, duration: 1.6), forKey: "pulse")
    }
}

// MARK: - Routes map: curves, marching dashes, particles

struct FlowRoute: Equatable {
    var from: CGPoint
    var to: CGPoint
    var color: Color
    var particles: Int
}

struct FlowLayer: NSViewRepresentable {
    let routes: [FlowRoute]

    func makeNSView(context: Context) -> FlowLayerView { FlowLayerView() }
    func updateNSView(_ view: FlowLayerView, context: Context) { view.update(routes) }
}

final class FlowLayerView: CAHostView {
    private var routes: [FlowRoute] = []
    private var built: [FlowRoute]?

    func update(_ r: [FlowRoute]) {
        guard r != routes else { return }
        routes = r
        rebuild()
    }

    override func restartAnimations() { built = nil; rebuild() }

    static func curve(_ a: CGPoint, _ b: CGPoint) -> CGPath {
        let p = CGMutablePath()
        let mid = (a.x + b.x) / 2
        p.move(to: a)
        p.addCurve(to: b, control1: CGPoint(x: mid, y: a.y), control2: CGPoint(x: mid, y: b.y))
        return p
    }

    private func rebuild() {
        guard built != routes else { return }
        built = routes
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        root.sublayers?.forEach { $0.removeFromSuperlayer() }
        for (i, r) in routes.enumerated() {
            let path = Self.curve(r.from, r.to)
            let base = CAShapeLayer()
            base.path = path
            base.fillColor = nil
            base.strokeColor = cgColor(r.color, 0.08)
            base.lineWidth = 12
            base.lineCap = .round
            root.addSublayer(base)

            let dash = CAShapeLayer()
            dash.path = path
            dash.fillColor = nil
            dash.strokeColor = cgColor(r.color, 0.7)
            dash.lineWidth = 2
            dash.lineCap = .round
            dash.lineDashPattern = [3, 13]
            let march = CABasicAnimation(keyPath: "lineDashPhase")
            march.fromValue = 0
            march.toValue = -16
            dash.add(.forever(march, duration: 0.9), forKey: "march")
            root.addSublayer(dash)

            for k in 0..<r.particles {
                let p = CALayer()
                p.bounds = CGRect(x: 0, y: 0, width: 8, height: 8)
                p.cornerRadius = 4
                p.backgroundColor = NSColor.white.withAlphaComponent(0.95).cgColor
                p.shadowColor = cgColor(r.color)
                p.shadowOpacity = 1
                p.shadowRadius = 6
                p.shadowOffset = .zero
                p.shadowPath = CGPath(ellipseIn: p.bounds, transform: nil)  // no per-frame shadow shape pass
                p.position = r.from
                p.opacity = 0
                let move = CAKeyframeAnimation(keyPath: "position")
                move.path = path
                move.calculationMode = .paced
                let fade = CAKeyframeAnimation(keyPath: "opacity")
                fade.values = [0, 1, 1, 0]
                fade.keyTimes = [0, 0.1, 0.9, 1]
                let g = CAAnimationGroup()
                g.animations = [move, fade]
                g.timingFunction = CAMediaTimingFunction(controlPoints: 0.45, 0.05, 0.55, 0.95)
                let period = 2.6
                let offset = period * (Double(k) / Double(r.particles)) + Double(i) * 0.37
                p.add(.forever(g, duration: period, offset: offset), forKey: "flow")
                root.addSublayer(p)
            }
        }
        CATransaction.commit()
    }
}

// MARK: - Small decorative effects

/// A rounded outline whose opacity breathes; for live server nodes.
struct BreathingRim: NSViewRepresentable {
    let cornerRadius: CGFloat
    let active: Bool

    func makeNSView(context: Context) -> BreathingRimView { BreathingRimView() }
    func updateNSView(_ view: BreathingRimView, context: Context) {
        view.cornerRadius = cornerRadius
        view.setActive(active)
    }
}

final class BreathingRimView: CAHostView {
    private let rim = CAShapeLayer()
    var cornerRadius: CGFloat = 18
    private var active: Bool?

    override init(frame: NSRect) {
        super.init(frame: frame)
        rim.fillColor = nil
        rim.strokeColor = NSColor.white.cgColor
        rim.lineWidth = 1.2
        rim.opacity = 0.18
        root.addSublayer(rim)
    }

    override func layoutLayers() {
        rim.frame = bounds
        rim.path = CGPath(roundedRect: bounds.insetBy(dx: 0.6, dy: 0.6), cornerWidth: cornerRadius, cornerHeight: cornerRadius, transform: nil)
    }

    func setActive(_ a: Bool) {
        guard a != active else { return }
        active = a
        restartAnimations()
    }

    override func restartAnimations() {
        rim.removeAllAnimations()
        guard active == true else { return }
        let b = CABasicAnimation(keyPath: "opacity")
        b.fromValue = 0.18
        b.toValue = 0.5
        b.autoreverses = true
        b.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
        rim.add(.forever(b, duration: 1.4), forKey: "breathe")
    }
}

/// A soft band of light sweeping across (horizontal) or down (vertical) the view.
struct SweepingLight: NSViewRepresentable {
    enum Axis { case horizontal, vertical }
    let axis: Axis
    let color: Color
    let thickness: CGFloat
    let period: CFTimeInterval
    var active = true

    func makeNSView(context: Context) -> SweepingLightView { SweepingLightView() }
    func updateNSView(_ view: SweepingLightView, context: Context) {
        view.configure(axis: axis, color: cgColor(color), thickness: thickness, period: period, active: active)
    }
}

final class SweepingLightView: CAHostView {
    private let band = CAGradientLayer()
    private var axis: SweepingLight.Axis = .horizontal
    private var thickness: CGFloat = 120
    private var period: CFTimeInterval = 3
    private var active = true
    private var lastSize: CGSize = .zero

    override init(frame: NSRect) {
        super.init(frame: frame)
        root.masksToBounds = true
        root.addSublayer(band)
    }

    func configure(axis: SweepingLight.Axis, color: CGColor, thickness: CGFloat, period: CFTimeInterval, active: Bool) {
        let changed = axis != self.axis || thickness != self.thickness || period != self.period || active != self.active
        self.axis = axis
        self.thickness = thickness
        self.period = period
        self.active = active
        band.colors = [color.copy(alpha: 0)!, color, color.copy(alpha: 0)!]
        if axis == .horizontal {
            band.startPoint = CGPoint(x: 0, y: 0.5)
            band.endPoint = CGPoint(x: 1, y: 0.5)
        } else {
            band.startPoint = CGPoint(x: 0.5, y: 0)
            band.endPoint = CGPoint(x: 0.5, y: 1)
        }
        if changed { restartAnimations() }
    }

    override func layoutLayers() {
        if axis == .horizontal {
            band.bounds = CGRect(x: 0, y: 0, width: thickness, height: bounds.height)
        } else {
            band.bounds = CGRect(x: 0, y: 0, width: bounds.width, height: thickness)
        }
        if bounds.size != lastSize {
            lastSize = bounds.size
            restartAnimations()
        }
    }

    override func restartAnimations() {
        band.removeAllAnimations()
        band.isHidden = !active
        guard active, bounds.width > 0 else { return }
        let move = CABasicAnimation(keyPath: "position")
        if axis == .horizontal {
            move.fromValue = CGPoint(x: -thickness / 2, y: bounds.midY)
            move.toValue = CGPoint(x: bounds.width + thickness / 2, y: bounds.midY)
        } else {
            move.fromValue = CGPoint(x: bounds.midX, y: -thickness / 2)
            move.toValue = CGPoint(x: bounds.midX, y: bounds.height + thickness / 2)
        }
        move.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
        band.position = CGPoint(x: -1000, y: -1000)
        band.add(.forever(move, duration: period), forKey: "sweep")
    }
}

/// Dashed rounded border whose dashes march around.
struct MarchingBorder: NSViewRepresentable {
    let cornerRadius: CGFloat
    let color: Color

    func makeNSView(context: Context) -> MarchingBorderView { MarchingBorderView() }
    func updateNSView(_ view: MarchingBorderView, context: Context) {
        view.cornerRadius = cornerRadius
        view.border.strokeColor = cgColor(color)
    }
}

final class MarchingBorderView: CAHostView {
    let border = CAShapeLayer()
    var cornerRadius: CGFloat = 18

    override init(frame: NSRect) {
        super.init(frame: frame)
        border.fillColor = nil
        border.lineWidth = 1.5
        border.lineDashPattern = [8, 8]
        root.addSublayer(border)
    }

    override func layoutLayers() {
        border.frame = bounds
        border.path = CGPath(roundedRect: bounds.insetBy(dx: 0.75, dy: 0.75), cornerWidth: cornerRadius, cornerHeight: cornerRadius, transform: nil)
    }

    override func restartAnimations() {
        border.removeAllAnimations()
        let march = CABasicAnimation(keyPath: "lineDashPhase")
        march.fromValue = 0
        march.toValue = -16
        border.add(.forever(march, duration: 1), forKey: "march")
    }
}
