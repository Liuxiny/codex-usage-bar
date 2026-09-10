import SwiftUI
import AppKit
import UsageCore

@MainActor struct SettingsView: View {
    @ObservedObject var controller: Controller
    @State private var prefs: Preferences
    @State private var provider = Provider()
    @State private var result = ""
    @State private var testing = false
    @State private var keychainLoaded = false
    @State private var dirtyProvider = false
    init(controller: Controller) {
        self.controller = controller; _prefs = State(initialValue: controller.preferences)
    }
    private func text(_ zh: String, _ en: String) -> String { controller.text(zh, en) }
    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section(text("连接参数", "Connection")) {
                    Picker(text("数据源", "Source"), selection: $prefs.source) {
                        Text(text("自动跟随 CC Switch", "Follow CC Switch automatically")).tag("auto")
                        Text(text("仅官方", "Official only")).tag("official")
                        Text(text("仅 CC Switch", "CC Switch only")).tag("ccswitch")
                        Text(text("独立配置", "Standalone")).tag("standalone")
                    }
                    if prefs.source == "auto" || prefs.source == "ccswitch" {
                        TextField(text("CC Switch 数据目录", "CC Switch data directory"), text: $prefs.ccDirectory)
                        HStack {
                            Button(text("自动查找", "Auto detect")) {
                                do { prefs.ccDirectory = try CCSwitch.directory().path } catch { result = text("无法读取目录覆盖配置", "Cannot read directory override") }
                            }
                            Button(text("浏览…", "Browse…")) {
                                let picker = NSOpenPanel(); picker.canChooseDirectories = true; picker.canChooseFiles = false; picker.allowsMultipleSelection = false
                                if picker.runModal() == .OK { prefs.ccDirectory = picker.url?.path ?? "" }
                            }
                            Text(text("选择包含 cc-switch.db 的文件夹", "Choose the folder containing cc-switch.db")).font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    if prefs.source != "ccswitch" && prefs.source != "standalone" {
                        TextField(text("Codex CLI 路径（留空自动查找）", "Codex CLI path (blank: auto detect)"), text: $prefs.cliPath)
                    }
                    if prefs.source == "standalone" {
                        TextField(text("显示名称", "Display name"), text: $provider.name)
                        TextField("Base URL", text: $provider.baseUrl)
                        SecureField("API Key", text: $provider.apiKey)
                        SecureField("Access Token", text: $provider.accessToken)
                        TextField("User ID", text: $provider.userId)
                        Stepper(text("刷新间隔：\(provider.interval) 分钟（0 为手动）", "Refresh: \(provider.interval) min (0 = manual)"), value: $provider.interval, in: 0...1440)
                        Stepper(text("请求超时：\(provider.timeout) 秒", "Timeout: \(provider.timeout) seconds"), value: $provider.timeout, in: 2...30)
                    }
                }
                if prefs.source == "standalone" {
                    Section(text("查询脚本", "Usage script")) {
                        TextEditor(text: $provider.code).font(.system(size: 12, design: .monospaced)).frame(minHeight: 180)
                            .onChange(of: provider) { _ in if keychainLoaded { dirtyProvider = true } }
                        Button(text("填入套餐模板", "Use package template")) {
                            if let url = Bundle.main.url(forResource: "package-quota", withExtension: "js"), let code = try? String(contentsOf: url, encoding: .utf8) { provider.code = code }
                            else { result = text("套餐模板缺失，请使用正式构建包", "Template missing; use the packaged app") }
                        }
                    }
                }
                Section(text("测试结果", "Test result")) {
                    HStack {
                        Button(testing ? text("查询中…", "Querying…") : text("测试查询", "Test query")) { test() }.disabled(testing)
                        if testing { ProgressView().controlSize(.small) }
                    }
                    Text(result.isEmpty ? text("保存前可先测试连接和用量脚本。", "Test the connection and usage script before saving.") : result)
                        .textSelection(.enabled).font(.system(size: 12, design: .monospaced)).frame(maxWidth: .infinity, alignment: .leading)
                }
            }.formStyle(.grouped)
            Divider()
            HStack {
                Spacer()
                Button(text("保存", "Save")) {
                    do {
                        // A failed Keychain read must never overwrite stored credentials.
                        if prefs.source == "standalone" && !keychainLoaded { throw UsageError.message("Keychain unavailable") }
                        try controller.apply(prefs, provider: prefs.source == "standalone" && (dirtyProvider || provider.code.isEmpty == false) ? provider : nil)
                        result = text("已保存", "Saved")
                    } catch { result = text("保存失败，请检查钥匙串访问权限和数据目录。", "Save failed; check Keychain access and data directory.") }
                }.keyboardShortcut(.defaultAction).disabled(testing)
            }.padding(16)
        }
        .onAppear {
            do { provider = try CredentialStore.load(); keychainLoaded = true }
            catch { result = text("无法读取钥匙串，独立配置暂不能保存。", "Cannot read Keychain; standalone saving is disabled.") }
        }
    }
    private func test() {
        testing = true; result = ""
        let selectedPrefs = prefs, standalone = provider
        Task { @MainActor in
            defer { testing = false }
            do {
                let selected: Provider?
                if selectedPrefs.source == "standalone" { selected = standalone }
                else if selectedPrefs.source == "official" { selected = nil }
                else { selected = try await Task.detached { try CCSwitch.current(selectedPrefs.ccDirectory, required: selectedPrefs.source == "ccswitch") }.value }
                let rows: [UsageRow]
                if let selected, !selected.official {
                    let request = try await ScriptRunner.request(selected)
                    let response = try await UsageHTTP().fetch(request, timeout: selected.timeout)
                    rows = try await ScriptRunner.extract(selected, response: response)
                } else {
                    let server = AppServer(); defer { server.stop() }
                    try await server.start(custom: selectedPrefs.cliPath)
                    rows = UsageParser.official(try await server.request("account/rateLimits/read"))
                }
                result = rows.isEmpty ? text("未返回用量", "No usage returned") : rows.map {
                    "\($0.name): \($0.valid ? ($0.remaining.map { String(format: "%.2f", $0) } ?? "—") + " " + $0.unit : text("不可用", "Unavailable"))\n\($0.detail)"
                }.joined(separator: "\n\n")
            } catch {
                // Network error descriptions can contain URLs. Use a safe UI message.
                result = text("测试失败。请检查数据目录、脚本、凭据、网络或 Codex CLI 登录状态。", "Test failed. Check the data directory, script, credentials, network, or Codex CLI login.")
            }
        }
    }
}
