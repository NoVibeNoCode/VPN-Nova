// Renders the app icon (nova orb on a dark squircle) into an .iconset.
// Usage: swift make-icon.swift <out.iconset>
import AppKit

let out = URL(fileURLWithPath: CommandLine.arguments[1])
try? FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)

func color(_ hex: UInt32, _ a: CGFloat = 1) -> NSColor {
    NSColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255, blue: CGFloat(hex & 0xFF) / 255, alpha: a)
}

func render(_ px: Int) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8, samplesPerPixel: 4,
                               hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
    let ctx = NSGraphicsContext.current!.cgContext
    let s = CGFloat(px)

    // Squircle background.
    let inset = s * 0.09
    let rect = CGRect(x: inset, y: inset, width: s - 2 * inset, height: s - 2 * inset)
    let bg = NSBezierPath(roundedRect: rect, xRadius: rect.width * 0.23, yRadius: rect.width * 0.23)
    NSGradient(colors: [color(0x141733), color(0x07080F)])!.draw(in: bg, angle: -90)
    bg.addClip()

    // Glow.
    let c = CGPoint(x: s / 2, y: s / 2)
    let glow = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                          colors: [color(0x8B7CFF, 0.55).cgColor, color(0xFF7A59, 0.18).cgColor, color(0x07080F, 0).cgColor] as CFArray,
                          locations: [0, 0.55, 1])!
    ctx.drawRadialGradient(glow, startCenter: c, startRadius: 0, endCenter: c, endRadius: s * 0.46, options: [])

    // Rings.
    for (i, r) in [0.36, 0.30].enumerated() {
        ctx.setStrokeColor(color(0x8B7CFF, i == 0 ? 0.25 : 0.45).cgColor)
        ctx.setLineWidth(s * 0.008)
        ctx.strokeEllipse(in: CGRect(x: c.x - s * r, y: c.y - s * r, width: 2 * s * r, height: 2 * s * r))
    }

    // Core orb.
    let rCore = s * 0.22
    ctx.saveGState()
    ctx.addEllipse(in: CGRect(x: c.x - rCore, y: c.y - rCore, width: 2 * rCore, height: 2 * rCore))
    ctx.clip()
    let core = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                          colors: [color(0xC9C1FF).cgColor, color(0x5B4BE6).cgColor, color(0x1F1766).cgColor] as CFArray,
                          locations: [0, 0.55, 1])!
    let hl = CGPoint(x: c.x - rCore * 0.32, y: c.y + rCore * 0.45)
    ctx.drawRadialGradient(core, startCenter: hl, startRadius: 0, endCenter: c, endRadius: rCore * 1.15, options: [.drawsAfterEndLocation])
    ctx.restoreGState()

    // Four-point star.
    let star = NSBezierPath()
    let R = s * 0.12, r = s * 0.025
    for k in 0..<8 {
        let a = CGFloat(k) * .pi / 4 + .pi / 2
        let rad = k % 2 == 0 ? R : r
        let p = CGPoint(x: c.x + cos(a) * rad, y: c.y + sin(a) * rad)
        if k == 0 { star.move(to: p) } else { star.line(to: p) }
    }
    star.close()
    color(0xFFFFFF, 0.95).setFill()
    star.fill()

    // Sparks.
    for (dx, dy, rr) in [(0.25, 0.22, 0.012), (-0.27, -0.18, 0.009), (0.2, -0.27, 0.007)] {
        let p = CGRect(x: c.x + s * dx - s * rr, y: c.y + s * dy - s * rr, width: 2 * s * rr, height: 2 * s * rr)
        color(0xFFFFFF, 0.85).setFill()
        NSBezierPath(ovalIn: p).fill()
    }

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

for size in [16, 32, 128, 256, 512] {
    try render(size).write(to: out.appendingPathComponent("icon_\(size)x\(size).png"))
    try render(size * 2).write(to: out.appendingPathComponent("icon_\(size)x\(size)@2x.png"))
}
