import AppKit
import ApplicationServices

// AX supplies standard-window identity and geometry, never usage or conversation
// content. File dialogs/sheets are excluded just as in Windows 0.7.6.
@MainActor final class WindowFollower {
    private weak var panel: OverlayPanel?
    private weak var controller: Controller?
    private var observedPID: pid_t = 0
    private var observer: AXObserver?
    private var observedWindows: [AXUIElement] = []
    private var target: AXUIElement?
    private var lastScan = Date.distantPast
    private var application: NSRunningApplication?
    private var lastFrontPID: pid_t = 0
    var codexRunning: Bool { application != nil && application?.isTerminated == false }

    init(panel: OverlayPanel, controller: Controller) { self.panel = panel; self.controller = controller }
    func stop() {
        if let observer { CFRunLoopRemoveSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(observer), .commonModes) }
        observer = nil; observedPID = 0; observedWindows = []; target = nil
    }
    private func value(_ element: AXUIElement, _ attribute: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success else { return nil }
        return value
    }
    private func standard(_ element: AXUIElement) -> Bool {
        guard value(element, kAXRoleAttribute) as? String == kAXWindowRole,
              value(element, kAXSubroleAttribute) as? String == kAXStandardWindowSubrole,
              let rect = rectangle(element), rect.width >= 320, rect.height >= 200 else { return false }
        return true
    }
    private func rectangle(_ element: AXUIElement) -> CGRect? {
        guard let position = value(element, kAXPositionAttribute), let size = value(element, kAXSizeAttribute),
              CFGetTypeID(position) == AXValueGetTypeID(), CFGetTypeID(size) == AXValueGetTypeID() else { return nil }
        var point = CGPoint.zero, dimensions = CGSize.zero
        guard AXValueGetValue(position as! AXValue, .cgPoint, &point), AXValueGetValue(size as! AXValue, .cgSize, &dimensions),
              point.x.isFinite, point.y.isFinite, dimensions.width.isFinite, dimensions.height.isFinite else { return nil }
        return CGRect(origin: point, size: dimensions)
    }
    private func scan(_ app: NSRunningApplication) {
        let axApp = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(axApp, 0.2)
        let windows = (value(axApp, kAXWindowsAttribute) as? [AXUIElement] ?? []).filter { standard($0) }
        if let main = value(axApp, kAXMainWindowAttribute), CFGetTypeID(main) == AXUIElementGetTypeID(), standard(main as! AXUIElement) {
            target = (main as! AXUIElement)
        } else if let previous = target, windows.contains(where: { CFEqual($0, previous) }) { target = previous }
        else { target = windows.first }
        if observedPID != app.processIdentifier || observer == nil {
            let selectedTarget = target
            stop(); observedPID = app.processIdentifier
            var created: AXObserver?
            let callback: AXObserverCallback = { _, _, _, context in
                guard let context else { return }
                let follower = Unmanaged<WindowFollower>.fromOpaque(context).takeUnretainedValue()
                Task { @MainActor in follower.lastScan = .distantPast; follower.update() }
            }
            if AXObserverCreate(app.processIdentifier, callback, &created) == .success, let created {
                observer = created
                CFRunLoopAddSource(CFRunLoopGetMain(), AXObserverGetRunLoopSource(created), .commonModes)
                for name in [kAXWindowCreatedNotification, kAXMainWindowChangedNotification, kAXFocusedWindowChangedNotification] {
                    AXObserverAddNotification(created, axApp, name as CFString, Unmanaged.passUnretained(self).toOpaque())
                }
            }
            // stop() cleared the cached target when changing applications.
            target = selectedTarget
        }
        if let observer {
            let names = [kAXMovedNotification, kAXResizedNotification, kAXWindowMiniaturizedNotification, kAXWindowDeminiaturizedNotification, kAXUIElementDestroyedNotification]
            for old in observedWindows where !windows.contains(where: { CFEqual($0, old) }) {
                for name in names { AXObserverRemoveNotification(observer, old, name as CFString) }
            }
            for window in windows where !observedWindows.contains(where: { CFEqual($0, window) }) {
                for name in names { AXObserverAddNotification(observer, window, name as CFString, Unmanaged.passUnretained(self).toOpaque()) }
            }
            observedWindows = windows
        }
    }
    func update() {
        guard let panel, let controller else { return }
        if Date().timeIntervalSince(lastScan) >= 3 {
            lastScan = Date()
            application = NSWorkspace.shared.runningApplications.first {
                !$0.isTerminated && ($0.bundleIdentifier == "com.openai.codex" || ($0.bundleURL?.lastPathComponent == "Codex.app" && $0.localizedName == "Codex"))
            }
            if controller.preferences.follow, AXIsProcessTrusted(), let application { scan(application) }
            else if !codexRunning { stop() }
        }
        guard controller.preferences.visible, codexRunning, controller.connected || controller.thirdParty else { panel.orderOut(nil); return }
        if !controller.preferences.follow {
            panel.level = .floating
            let screen = NSScreen.screens.first { $0.visibleFrame.intersects(panel.frame) } ?? NSScreen.main
            if let visible = screen?.visibleFrame {
                let p = panel.frame.origin
                panel.setFrameOrigin(NSPoint(x: min(max(p.x, visible.minX), visible.maxX - panel.frame.width), y: min(max(p.y, visible.minY), visible.maxY - panel.frame.height)))
            }
            if !panel.isVisible { panel.orderFrontRegardless() }
            return
        }
        guard AXIsProcessTrusted(), let app = application, !app.isHidden, let target, standard(target),
              value(target, kAXMinimizedAttribute) as? Bool != true, let rect = rectangle(target) else { panel.orderOut(nil); return }
        // AX and CG use the primary display's top-left; AppKit uses its bottom-left.
        let primaryTop = NSScreen.screens.first?.frame.maxY ?? 0
        let origin = NSPoint(x: rect.midX - panel.frame.width / 2, y: primaryTop - rect.minY - 17.5 - panel.frame.height + 16.5)
        panel.setFrameOrigin(origin); panel.level = .normal
        let frontPID = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0
        if frontPID == app.processIdentifier {
            // Do not lift above Codex's file picker or sheet while it has focus.
            let axApp = AXUIElementCreateApplication(app.processIdentifier)
            let focused = value(axApp, kAXFocusedWindowAttribute)
            let children = (value(target, kAXChildrenAttribute) as? [AXUIElement]) ?? []
            let sheets = children.filter { value($0, kAXRoleAttribute) as? String == kAXSheetRole }
            let mainFocused = focused.map { CFEqual($0, target) } == true && sheets.isEmpty
            if mainFocused { panel.orderFrontRegardless() }
            else { orderAboveTarget(app, rect: rect) }
        } else if !panel.isVisible || lastFrontPID == app.processIdentifier {
            orderAboveTarget(app, rect: rect)
        }
        lastFrontPID = frontPID
    }
    private func orderAboveTarget(_ app: NSRunningApplication, rect: CGRect) {
        // Public WindowServer metadata, no screen pixels or private AX API.
        let windows = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] ?? []
        let match = windows.first { item in
            guard item[kCGWindowOwnerPID as String] as? Int32 == app.processIdentifier,
                  item[kCGWindowLayer as String] as? Int == 0,
                  let bounds = item[kCGWindowBounds as String] as? [String: Any],
                  let frame = CGRect(dictionaryRepresentation: bounds as CFDictionary) else { return false }
            return abs(frame.minX - rect.minX) < 3 && abs(frame.minY - rect.minY) < 3 && abs(frame.width - rect.width) < 3 && abs(frame.height - rect.height) < 3
        }
        guard let id = match?[kCGWindowNumber as String] as? Int else { panel?.orderOut(nil); return }
        panel?.order(.above, relativeTo: id)
    }
}
