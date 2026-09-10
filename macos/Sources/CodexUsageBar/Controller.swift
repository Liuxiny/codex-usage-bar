import AppKit
import SwiftUI
import ServiceManagement
import ApplicationServices
import UsageCore

@MainActor final class Controller: NSObject, ObservableObject, NSApplicationDelegate {
    @Published var preferences = Preferences.load()
    @Published var rows: [UsageRow] = []
    @Published var status = ""
    @Published var sourceName = "Codex"
    @Published var yesterday: Double?
    @Published var lifetime: Double?
    var thirdParty = false
    var connected = false
    var theme = CodexTheme.load()
    private var provider: Provider?
    private var standalone: Provider?
    private var sourceKey = ""
    private var generation = 0
    private var sourceBusy = false
    private var refreshing = false
    private var refreshAgain = false
    private var nextQuery = Date.distantPast
    private var nextTokens = Date.distantPast
    private var retryIndex = 0
    private var cache: (Data, Date)?
    private var cacheBusy = false
    private var timer: Timer?
    private var lastConfig = Date.distantPast
    private var lastExtract = Date.distantPast
    private let server = AppServer()
    private var statusItem: NSStatusItem!
    private var overlay: OverlayPanel!
    private var follower: WindowFollower!
    private var settingsWindow: NSWindow?

    var chinese: Bool { preferences.chinese }
    func text(_ zh: String, _ en: String) -> String { chinese ? zh : en }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let peers = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "com.codexusagebar.macos")
        if peers.contains(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) { NSApp.terminate(nil); return }
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = NSImage(systemSymbolName: "chart.pie", accessibilityDescription: "Codex Usage Bar")
        overlay = OverlayPanel(controller: self)
        follower = WindowFollower(panel: overlay, controller: self)
        server.updated = { [weak self] in Task { @MainActor in self?.refresh() } }
        server.exited = { [weak self] in Task { @MainActor in
            guard let self, !self.thirdParty else { return }
            self.connected = false; self.rows = []; self.cache = nil
            self.status = self.text("连接已断开", "Disconnected"); self.redraw()
        } }
        rebuildMenu()
        if preferences.follow && !AXIsProcessTrusted() && !UserDefaults.standard.bool(forKey: "accessibilityPrompted") {
            UserDefaults.standard.set(true, forKey: "accessibilityPrompted")
            follow()
        }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in Task { @MainActor in await self?.tick() } }
        Task { await tick() }
    }

    func applicationWillTerminate(_ notification: Notification) { timer?.invalidate(); server.stop(); follower?.stop() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func tick() async {
        follower.update()
        let now = Date()
        if now.timeIntervalSince(lastConfig) >= 5, !sourceBusy {
            sourceBusy = true; lastConfig = now
            theme = CodexTheme.load(); NSApp.appearance = theme.appearance; overlay.appearance = theme.appearance; settingsWindow?.appearance = theme.appearance
            await resolveSource(); sourceBusy = false
        }
        guard follower.codexRunning else {
            if server.alive { server.stop() }
            connected = false
            if !thirdParty { rows = []; nextQuery = .distantPast }
            status = text("等待 Codex 启动", "Waiting for Codex"); redraw(); return
        }
        if now >= nextQuery { refresh() }
        if thirdParty, now.timeIntervalSince(lastExtract) >= 15, !refreshing, !cacheBusy, let provider, let cache {
            lastExtract = now
            if now.timeIntervalSince(cache.1) > max(360, Double(provider.interval) * 120) {
                self.cache = nil; rows = []; connected = false
                status = text("缓存已过期，请刷新", "Cached usage expired; refresh"); redraw()
            } else {
                cacheBusy = true; let current = generation
                do {
                    let extracted = try await ScriptRunner.extract(provider, response: cache.0)
                    if current == generation && self.cache?.1 == cache.1 { rows = extracted; redraw() }
                } catch {
                    if current == generation && self.cache?.1 == cache.1 { self.cache = nil; rows = []; connected = false; status = text("脚本重新计算失败", "Extractor failed"); redraw() }
                }
                cacheBusy = false
            }
        }
    }

    private func resolveSource() async {
        let prefs = preferences
        do {
            let selected: Provider?
            if prefs.source == "standalone" {
                if standalone == nil { standalone = try CredentialStore.load() }
                selected = standalone
            } else if prefs.source == "official" { selected = nil }
            else {
                selected = try await Task.detached(priority: .utility) { try CCSwitch.current(prefs.ccDirectory, required: prefs.source == "ccswitch") }.value
            }
            guard prefs.source == preferences.source, prefs.ccDirectory == preferences.ccDirectory else { return }
            let key = prefs.source + "|" + prefs.cliPath
            if key != sourceKey || selected != provider {
                generation += 1; server.stop(); provider = selected; sourceKey = key
                thirdParty = selected != nil && selected?.official != true
                sourceName = thirdParty ? selected!.name : "Codex"
                cache = nil; rows = []; yesterday = nil; lifetime = nil; connected = false
                retryIndex = 0; nextQuery = .distantPast; nextTokens = .distantPast
                status = text("正在连接…", "Connecting…"); redraw()
            }
        } catch {
            guard prefs.source == preferences.source, prefs.ccDirectory == preferences.ccDirectory else { return }
            generation += 1; server.stop(); provider = nil; sourceKey = "error"
            thirdParty = true; sourceName = prefs.source == "standalone" ? "Custom API" : "CC Switch"
            cache = nil; rows = []; connected = false; nextQuery = .distantFuture
            status = text("数据源读取失败，请检查设置", "Cannot read source; check settings"); redraw()
        }
    }

    func refresh() {
        guard !refreshing else { refreshAgain = true; return }
        guard !sourceBusy && !sourceKey.isEmpty else { nextQuery = .distantPast; return }
        guard follower.codexRunning else { return }
        if sourceKey == "error" { lastConfig = .distantPast; return }
        refreshing = true
        let current = generation, selected = provider, isThird = thirdParty
        nextQuery = .distantFuture
        Task {
            defer {
                refreshing = false
                if refreshAgain || current != generation { refreshAgain = false; nextQuery = .distantPast }
            }
            do {
                let result: [UsageRow]
                if isThird, let selected {
                    let request = try await ScriptRunner.request(selected)
                    let response = try await UsageHTTP().fetch(request, timeout: selected.timeout)
                    result = try await ScriptRunner.extract(selected, response: response)
                    guard current == generation else { return }
                    cache = (response, Date()); lastExtract = Date()
                } else {
                    if !server.alive { try await server.start(custom: preferences.cliPath) }
                    guard current == generation else { return }
                    result = UsageParser.official(try await server.request("account/rateLimits/read"))
                    guard !result.isEmpty else { throw UsageError.message("No official limits") }
                    if Date() >= nextTokens {
                        if let usage = try? await server.request("account/usage/read"), current == generation {
                            (yesterday, lifetime) = UsageParser.tokens(usage)
                        }
                        nextTokens = Date().addingTimeInterval(600)
                    }
                }
                guard current == generation else { return }
                rows = result; connected = true; retryIndex = 0; status = text("连接成功", "Connected")
                nextQuery = isThird && selected?.interval == 0 ? .distantFuture : Date().addingTimeInterval(Double(isThird ? (selected?.interval ?? 2) * 60 : 120))
            } catch {
                guard current == generation else { return }
                connected = false; rows = []; cache = nil
                status = text("查询失败，请检查数据源或登录", "Query failed; check source or login")
                if isThird {
                    nextQuery = selected?.interval == 0 ? .distantFuture : Date().addingTimeInterval(Double((selected?.interval ?? 2) * 60))
                } else {
                    server.stop(); let delays = [1.0, 2, 5, 15, 30]
                    nextQuery = Date().addingTimeInterval(delays[min(retryIndex, 4)]); retryIndex += 1
                }
            }
            redraw()
        }
    }

    func apply(_ prefs: Preferences, provider: Provider?) throws {
        // Save credentials only on explicit settings save, never on source polling.
        if let provider { try CredentialStore.save(provider); standalone = provider }
        try prefs.save(); preferences = prefs
        generation += 1; sourceKey = ""; lastConfig = .distantPast
        overlay.expanded = false; redraw()
        Task { await tick() }
    }
    func savePosition(_ point: NSPoint) {
        preferences.x = point.x; preferences.y = point.y; try? preferences.save()
    }
    func redraw() { overlay?.refreshLayout(); follower?.update(); rebuildMenu() }

    func rebuildMenu() {
        guard statusItem != nil else { return }
        let menu = NSMenu()
        func item(_ title: String, _ action: Selector?, checked: Bool = false) -> NSMenuItem {
            let value = NSMenuItem(title: title, action: action, keyEquivalent: ""); value.target = self
            value.state = checked ? .on : .off; return value
        }
        menu.addItem(item("\(sourceName) · \(status)", nil)); menu.addItem(.separator())
        if preferences.follow && !AXIsProcessTrusted() {
            menu.addItem(item(text("跟随模式需要辅助功能权限", "Follow mode requires Accessibility access"), #selector(follow)))
        }
        menu.addItem(item(text("显示悬浮窗", "Show overlay"), #selector(toggleVisible), checked: preferences.visible))
        menu.addItem(item(text("独立展示", "Independent"), #selector(independent), checked: !preferences.follow))
        menu.addItem(item(text("跟随 Codex", "Follow Codex"), #selector(follow), checked: preferences.follow))
        menu.addItem(.separator())
        menu.addItem(item(text("数据源…", "Data source…"), #selector(showSettings)))
        let language = item(text("语言", "Language"), nil), languages = NSMenu()
        for (title, code) in [(text("跟随系统", "System"), "system"), ("中文", "zh"), ("English", "en")] {
            let value = item(title, #selector(setLanguage(_:)), checked: preferences.language == code)
            value.representedObject = code; languages.addItem(value)
        }
        language.submenu = languages; menu.addItem(language)
        menu.addItem(item(text("立即刷新", "Refresh now"), #selector(refreshNow)))
        menu.addItem(item(text("开机启动", "Launch at login"), #selector(toggleLogin), checked: SMAppService.mainApp.status == .enabled))
        menu.addItem(.separator())
        menu.addItem(item(text("退出", "Quit"), #selector(quit)))
        statusItem.menu = menu
        statusItem.button?.toolTip = "Codex Usage Bar 0.7.6 · " + status
    }
    @objc private func toggleVisible() { preferences.visible.toggle(); try? preferences.save(); redraw() }
    @objc private func independent() { preferences.follow = false; try? preferences.save(); redraw() }
    @objc private func follow() {
        if !AXIsProcessTrusted() {
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
        }
        preferences.follow = true; try? preferences.save(); redraw()
    }
    @objc private func setLanguage(_ sender: NSMenuItem) {
        preferences.language = (sender.representedObject as? String) ?? "system"
        try? preferences.save(); redraw()
    }
    @objc private func refreshNow() { refresh() }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch { alert(text("无法更改登录项，请先将应用放入“应用程序”文件夹，并检查系统设置中的登录项。", "Cannot change login item. Move the app to Applications and check System Settings → Login Items.")) }
        rebuildMenu()
    }
    func alert(_ message: String) { let alert = NSAlert(); alert.messageText = "Codex Usage Bar"; alert.informativeText = message; alert.runModal() }
    @objc func showSettings() {
        if let settingsWindow, settingsWindow.isVisible { settingsWindow.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return }
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 720, height: 720), styleMask: [.titled, .closable, .resizable], backing: .buffered, defer: false)
        window.title = text("Codex Usage Bar · 数据源", "Codex Usage Bar · Data source")
        window.isReleasedWhenClosed = false; window.appearance = theme.appearance
        window.contentView = NSHostingView(rootView: SettingsView(controller: self))
        window.minSize = NSSize(width: 640, height: 580); window.center(); settingsWindow = window
        window.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true)
    }
}
