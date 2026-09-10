import AppKit
import UsageCore

final class OverlayPanel: NSPanel {
    weak var controller: Controller?
    var expanded = false { didSet { refreshLayout() } }
    private var drawing: OverlayDrawing!
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
    init(controller: Controller) {
        self.controller = controller
        super.init(contentRect: NSRect(x: 0, y: 0, width: 320, height: 33), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false; backgroundColor = .clear; hasShadow = true
        hidesOnDeactivate = false; isReleasedWhenClosed = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        drawing = OverlayDrawing(panel: self); contentView = drawing
        setAccessibilityLabel("Codex Usage Bar")
        let visible = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1200, height: 800)
        setFrameOrigin(NSPoint(x: controller.preferences.x ?? visible.midX - 160, y: controller.preferences.y ?? visible.maxY - 65))
    }
    func refreshLayout() {
        guard drawing != nil else { return }
        let old = frame, size = drawing.preferredSize(expanded: expanded)
        setFrame(NSRect(x: old.minX, y: old.maxY - size.height, width: size.width, height: size.height), display: false)
        drawing.needsDisplay = true
    }
}

final class OverlayDrawing: NSView {
    weak var panel: OverlayPanel?
    private var tracking: NSTrackingArea?
    override var isFlipped: Bool { true }
    private var c: Controller { panel!.controller! }
    private var font: NSFont { uiFont(c.theme.fontSize) }
    private var menuFont: NSFont { uiFont(16) }
    private var ink: NSColor { c.theme.foreground ?? .labelColor }
    private var accent: NSColor { c.theme.accent }
    private var line: CGFloat { ceil(font.ascender - font.descender + font.leading) + 2 }
    private var quotas: [UsageRow] {
        let entries = c.rows.filter { $0.valid && $0.kind == "quota" && $0.percent != nil }
        if c.thirdParty { return entries }
        let short = entries.filter { $0.name == "5h" }.min { ($0.percent ?? 100) < ($1.percent ?? 100) }
        let week = entries.filter { $0.name == "Week" }.min { ($0.percent ?? 100) < ($1.percent ?? 100) }
        if short != nil || week != nil { return [short, week].compactMap { $0 } }
        return Array(entries.prefix(2))
    }
    private var balance: UsageRow? { c.rows.first { $0.valid && $0.kind == "balance" && $0.remaining != nil } }
    private var estimate: UsageRow? { c.rows.first { $0.valid && $0.kind == "estimate" && $0.remaining != nil } }
    init(panel: OverlayPanel) { self.panel = panel; super.init(frame: .zero); setAccessibilityElement(true); setAccessibilityRole(.button) }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        tracking = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self)
        addTrackingArea(tracking!)
    }
    override func mouseExited(with event: NSEvent) { panel?.expanded = false }
    override func mouseDown(with event: NSEvent) {
        guard let panel else { return }
        let start = NSEvent.mouseLocation, origin = panel.frame.origin
        let local = convert(event.locationInWindow, from: nil)
        var dragged = false
        while let next = window?.nextEvent(matching: [.leftMouseDragged, .leftMouseUp]) {
            if next.type == .leftMouseUp { break }
            let delta = NSPoint(x: NSEvent.mouseLocation.x - start.x, y: NSEvent.mouseLocation.y - start.y)
            if abs(delta.x) + abs(delta.y) > 3 { dragged = true }
            if dragged && !c.preferences.follow { panel.setFrameOrigin(NSPoint(x: origin.x + delta.x, y: origin.y + delta.y)) }
        }
        if dragged && !c.preferences.follow { c.savePosition(panel.frame.origin) }
        else if !dragged && (panel.expanded == false || local.y <= 33) { panel.expanded.toggle(); c.redraw() }
    }
    override func accessibilityPerformPress() -> Bool { panel?.expanded.toggle(); return true }
    func uiFont(_ size: CGFloat) -> NSFont {
        if !c.theme.fontName.isEmpty, let font = NSFont(name: c.theme.fontName, size: size) { return font }
        return NSFont.systemFont(ofSize: size)
    }
    private func styled(_ text: String, font: NSFont, color: NSColor, numericColor: NSColor? = nil) -> NSAttributedString {
        let value = NSMutableAttributedString(string: text, attributes: [.font: font, .foregroundColor: color])
        let pattern = try! NSRegularExpression(pattern: #"[+-]?\d+(?:\.\d+)?(?:[%KMBT])?"#)
        let bold = NSFontManager.shared.convert(font, toHaveTrait: .boldFontMask)
        for match in pattern.matches(in: text, range: NSRange(location: 0, length: (text as NSString).length)) {
            value.addAttributes([.font: bold, .foregroundColor: numericColor ?? color], range: match.range)
        }
        return value
    }
    private func width(_ text: String, _ font: NSFont, styled bold: Bool = false) -> CGFloat {
        bold ? styled(text, font: font, color: ink).size().width : (text as NSString).size(withAttributes: [.font: font]).width
    }
    private func label(_ row: UsageRow) -> String {
        if row.name == "5h" || row.name.hasSuffix(" · 5小时") { return c.text("5 小时", "5 hours") }
        if row.name == "Week" || row.name.hasSuffix(" · 周") { return c.text("周", "Weekly") }
        return row.name
    }
    private func percent(_ row: UsageRow) -> String { String(format: "%.0f%%", row.percent ?? 0) }
    private func wallet(_ row: UsageRow) -> String {
        let value = String(format: "%.2f", row.remaining ?? 0).replacingOccurrences(of: #"\.?0+$"#, with: "", options: .regularExpression)
        return "$ " + value + " " + row.unit
    }
    private var summary: String {
        if !c.connected { return c.sourceName + " · " + c.status }
        if let invalid = c.rows.first(where: { !$0.valid && $0.kind != "estimate" && !$0.detail.isEmpty }) { return invalid.detail }
        if let balance { return wallet(balance) }
        if let row = c.rows.first(where: { $0.kind != "estimate" }), let remaining = row.remaining {
            return String(format: "%.2f", remaining) + " " + row.unit
        }
        return c.text("暂无用量数据", "Usage unavailable")
    }
    private func columnWidth(_ row: UsageRow) -> CGFloat {
        let reset = UsageFormat.reset(row.reset, chinese: c.chinese)
        let fullReset = UsageFormat.reset(row.reset, chinese: c.chinese, compact: false)
        return [112, 51 + width(percent(row), menuFont, styled: true) + width(reset, menuFont),
                20 + width(label(row), font) + 8 + width(percent(row), font, styled: true), 20 + width(fullReset, font)].max() ?? 112
    }
    func preferredSize(expanded: Bool) -> NSSize {
        var w = quotas.reduce(CGFloat(0)) { $0 + columnWidth($1) }
        if c.thirdParty, let balance { w += width(wallet(balance), menuFont, styled: true) + 18 }
        if quotas.isEmpty { w = max(w, width(summary, menuFont) + 24) }
        if c.thirdParty, estimate != nil { w = max(w, width(c.text("历史估算 · 非额外额度", "Historical estimate · not additional quota"), font) + 24) }
        if !c.thirdParty { w = max(w, width(tokenText, font, styled: true) + 24) }
        w = max(220, w)
        // Both states use the same measured width. Screen clamp is a last resort.
        w = min(w, (panel?.screen ?? NSScreen.main)?.visibleFrame.width ?? 1600)
        let base = quotas.isEmpty ? line * 2 + 20 : 33 + line * 2 + 20
        var h: CGFloat = 33
        if expanded {
            h = base
            if c.thirdParty {
                if balance != nil { h += line + 18 }
                if estimate != nil { h += line * 2 + 26 }
            } else { h += line + 18 }
        }
        return NSSize(width: ceil(w), height: ceil(h))
    }
    private var tokenText: String { c.text("昨日 ", "Yesterday ") + UsageFormat.compact(c.yesterday) + "  ·  " + c.text("累计 ", "Total ") + UsageFormat.compact(c.lifetime) + " Tokens" }
    private func drawText(_ text: String, _ rect: NSRect, font: NSFont, color: NSColor, bold: Bool = false, center: Bool = false, numeric: NSColor? = nil) {
        let value = bold ? styled(text, font: font, color: color, numericColor: numeric) : NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: color])
        let size = value.size()
        NSGraphicsContext.saveGraphicsState(); rect.clip()
        value.draw(at: NSPoint(x: center ? rect.midX - size.width / 2 : rect.minX, y: rect.midY - size.height / 2))
        NSGraphicsContext.restoreGraphicsState()
    }
    private func divider(_ y: CGFloat) { ink.withAlphaComponent(0.13).setStroke(); let p = NSBezierPath(); p.move(to: NSPoint(x: 10, y: y)); p.line(to: NSPoint(x: bounds.width - 10, y: y)); p.stroke() }
    private func progress(_ rect: NSRect, value: Double) {
        ink.withAlphaComponent(0.13).setFill(); NSBezierPath(roundedRect: rect, xRadius: 2, yRadius: 2).fill()
        var fill = rect; fill.size.width *= CGFloat(min(100, max(0, value)) / 100)
        accent.setFill(); NSBezierPath(roundedRect: fill, xRadius: 2, yRadius: 2).fill()
    }
    private func ring(_ center: NSPoint, value: Double) {
        let rect = NSRect(x: center.x - 7, y: center.y - 7, width: 14, height: 14)
        let track = NSBezierPath(ovalIn: rect); track.lineWidth = 4; ink.withAlphaComponent(0.15).setStroke(); track.stroke()
        guard value > 0 else { return }
        let arc = NSBezierPath(); arc.lineWidth = 4; arc.lineCapStyle = .round
        arc.appendArc(withCenter: center, radius: 7, startAngle: -90, endAngle: -90 + CGFloat(min(100, value)) * 3.6, clockwise: false)
        accent.setStroke(); arc.stroke()
    }
    override func draw(_ dirtyRect: NSRect) {
        guard panel?.controller != nil else { return }
        let shape = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 8, yRadius: 8)
        (c.theme.background ?? .windowBackgroundColor).setFill(); shape.fill()
        ink.withAlphaComponent(0.14).setStroke(); shape.stroke()
        setAccessibilityLabel(quotas.map { label($0) + " " + percent($0) }.joined(separator: ", ") + (c.thirdParty ? " " + summary : " " + tokenText))
        if panel?.expanded != true {
            if quotas.isEmpty { drawText(summary, bounds.insetBy(dx: 10, dy: 0), font: menuFont, color: ink, bold: balance != nil, center: true, numeric: balance != nil ? accent : nil); return }
            let walletWidth = c.thirdParty ? balance.map { width(wallet($0), menuFont, styled: true) + 18 } ?? 0 : 0
            let available = bounds.width - walletWidth, natural = quotas.reduce(CGFloat(0)) { $0 + columnWidth($1) }
            var x: CGFloat = 0
            for row in quotas {
                let w = columnWidth(row) + (available - natural) / CGFloat(quotas.count)
                ring(NSPoint(x: x + 19, y: 16.5), value: row.percent ?? 0)
                let pw = width(percent(row), menuFont, styled: true)
                drawText(percent(row), NSRect(x: x + 34, y: 0, width: pw, height: 33), font: menuFont, color: accent, bold: true)
                drawText(UsageFormat.reset(row.reset, chinese: c.chinese), NSRect(x: x + 41 + pw, y: 0, width: max(0, w - 51 - pw), height: 33), font: menuFont, color: ink.withAlphaComponent(0.7))
                x += w
                if x < available - 1 { let p = NSBezierPath(); p.move(to: NSPoint(x: x, y: 10)); p.line(to: NSPoint(x: x, y: 23)); ink.withAlphaComponent(0.13).setStroke(); p.stroke() }
            }
            if c.thirdParty, let balance { drawText(wallet(balance), NSRect(x: x + 4, y: 0, width: walletWidth - 10, height: 33), font: menuFont, color: ink, bold: true, numeric: accent) }
            return
        }
        var top: CGFloat = quotas.isEmpty ? line * 2 + 20 : 33 + line * 2 + 20
        if quotas.isEmpty { drawText(summary, NSRect(x: 10, y: 0, width: bounds.width - 20, height: top), font: font, color: ink, center: true) }
        else {
            let natural = quotas.reduce(CGFloat(0)) { $0 + columnWidth($1) }
            var left: CGFloat = 0
            for (i, row) in quotas.enumerated() {
                let w = c.thirdParty ? bounds.width / CGFloat(quotas.count) : columnWidth(row) + (bounds.width - natural) / CGFloat(quotas.count)
                let x = left + 10
                progress(NSRect(x: x, y: 14.5, width: w - 20, height: 4), value: row.percent ?? 0)
                let lw = width(label(row), font), pw = width(percent(row), font, styled: true)
                let reset = UsageFormat.reset(row.reset, chinese: c.chinese, compact: false)
                var offset = lw + 8
                if let range = reset.range(of: #"\d{1,2}:\d{2}"#, options: .regularExpression) {
                    let center = width(String(reset[..<range.lowerBound]), font) + width(String(reset[range]), font) / 2
                    let centered = center - pw / 2
                    if centered >= offset && centered + pw <= w - 20 { offset = centered }
                }
                drawText(label(row), NSRect(x: x, y: 41, width: lw, height: line), font: font, color: ink)
                drawText(percent(row), NSRect(x: x + offset, y: 41, width: max(0, w - 20 - offset), height: line), font: font, color: accent, bold: true)
                drawText(reset, NSRect(x: x, y: 45 + line, width: w - 20, height: line), font: font, color: ink.withAlphaComponent(0.7))
                if i < quotas.count - 1 {
                    let p = NSBezierPath(); p.move(to: NSPoint(x: x + w - 10, y: 8)); p.line(to: NSPoint(x: x + w - 10, y: top - 8)); ink.withAlphaComponent(0.13).setStroke(); p.stroke()
                }
                left += w
            }
        }
        if c.thirdParty {
            if let balance {
                divider(top); drawText(wallet(balance), NSRect(x: 10, y: top + 5, width: bounds.width - 20, height: line + 8), font: font, color: ink, bold: true, numeric: accent)
                top += line + 18
            }
            if let estimate {
                divider(top)
                let value = c.text("余额折合周额度 ", "Weekly quota equivalent ") + String(format: "%.2f%%", estimate.remaining ?? 0)
                drawText(value, NSRect(x: 10, y: top + 8, width: bounds.width - 20, height: line), font: font, color: ink, bold: true)
                drawText(c.text("历史估算 · 非额外额度", "Historical estimate · not additional quota"), NSRect(x: 10, y: top + line + 12, width: bounds.width - 20, height: line), font: font, color: ink.withAlphaComponent(0.7))
            }
        } else {
            divider(top); drawText(tokenText, NSRect(x: 10, y: top + 5, width: bounds.width - 20, height: line + 8), font: font, color: ink.withAlphaComponent(0.7), bold: true, numeric: accent)
        }
    }
}
