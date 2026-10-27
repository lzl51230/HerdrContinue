import AppKit

let destination = URL(fileURLWithPath: CommandLine.arguments[1])
for size in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let pixels = size * scale
        let image = NSImage(size: NSSize(width: pixels, height: pixels))
        image.lockFocus()
        let context = NSGraphicsContext.current!.cgContext
        context.scaleBy(x: CGFloat(pixels) / 1024, y: CGFloat(pixels) / 1024)
        let rect = NSRect(x: 46, y: 46, width: 932, height: 932)
        let background = NSBezierPath(roundedRect: rect, xRadius: 205, yRadius: 205)
        NSGradient(starting: NSColor(red: 0.10, green: 0.22, blue: 0.27, alpha: 1),
                   ending: NSColor(red: 0.03, green: 0.09, blue: 0.13, alpha: 1))!.draw(in: background, angle: -70)
        let terminal = NSBezierPath(roundedRect: NSRect(x: 215, y: 292, width: 594, height: 454), xRadius: 55, yRadius: 55)
        NSColor.white.withAlphaComponent(0.13).setFill(); terminal.fill()
        NSColor.white.withAlphaComponent(0.9).setStroke()
        let prompt = NSBezierPath()
        prompt.lineWidth = 38; prompt.lineCapStyle = .round; prompt.lineJoinStyle = .round
        prompt.move(to: NSPoint(x: 310, y: 595)); prompt.line(to: NSPoint(x: 395, y: 522)); prompt.line(to: NSPoint(x: 310, y: 449)); prompt.stroke()
        let underscore = NSBezierPath(); underscore.lineWidth = 32; underscore.lineCapStyle = .round
        underscore.move(to: NSPoint(x: 462, y: 450)); underscore.line(to: NSPoint(x: 570, y: 450)); underscore.stroke()
        let arc = NSBezierPath(); arc.lineWidth = 49; arc.lineCapStyle = .round
        NSColor(red: 0.25, green: 0.88, blue: 0.76, alpha: 1).setStroke()
        arc.appendArc(withCenter: NSPoint(x: 645, y: 338), radius: 145, startAngle: 35, endAngle: 315, clockwise: false); arc.stroke()
        let arrow = NSBezierPath()
        arrow.move(to: NSPoint(x: 740, y: 470)); arrow.line(to: NSPoint(x: 813, y: 361)); arrow.line(to: NSPoint(x: 676, y: 372)); arrow.close()
        NSColor(red: 0.25, green: 0.88, blue: 0.76, alpha: 1).setFill(); arrow.fill()
        image.unlockFocus()
        let bitmap = NSBitmapImageRep(data: image.tiffRepresentation!)!
        let suffix = scale == 2 ? "@2x" : ""
        try bitmap.representation(using: .png, properties: [:])!.write(to: destination.appendingPathComponent("icon_\(size)x\(size)\(suffix).png"))
    }
}
