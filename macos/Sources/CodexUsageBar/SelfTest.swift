import AppKit
import CSQLite
import UsageCore

enum SelfTest {
    static func check(_ condition: @autoclosure () -> Bool, _ message: String) throws {
        guard condition() else { throw UsageError.message(message) }
    }
    static func run() -> Never {
        Task { @MainActor in
            do {
                try database()
                try await rpc()
                try await HTTPFixture.test()
                let answer = try await ScriptRunner.evaluate("JSON.stringify({value: [1, 2, 3].find(x => x === 2), host: typeof require + '/' + typeof fetch})") as? [String: Any]
                try check(answer?["value"] as? Int == 2, "JavaScriptCore Array.find")
                try check(answer?["host"] as? String == "undefined/undefined", "No JS host objects")
                let start = Date()
                do {
                    _ = try await ScriptRunner.evaluate("while (true) {}")
                    throw UsageError.message("Infinite script unexpectedly returned")
                }
                catch { try check(Date().timeIntervalSince(start) >= 4.5 && Date().timeIntervalSince(start) < 10, "Hard script timeout") }
                let recovered = try await ScriptRunner.evaluate("JSON.stringify(7)")
                try check(number(recovered) == 7, "Recovery after timeout")
                try await template()
                try render()
                print("PASS: SQLite read-only selection; RPC handshake/errors/cancellation; HTTP/redirect/size cap; JS isolation/timeout/recovery; package extractor; 8 UI preview fixtures")
                exit(0)
            } catch {
                // Only synthetic test fixtures are used; no user credentials/database.
                fputs("SELF-TEST FAILED: \(error)\n", stderr); exit(1)
            }
        }
        RunLoop.main.run()
        exit(1)
    }
    static func mockServer() -> Never {
        while let line = readLine() {
            guard let message = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? [String: Any], let id = message["id"] else { continue }
            let method = (message["method"] as? String) ?? ""
            if method == "never-reply" { continue }
            var reply: [String: Any] = ["id": id]
            if method == "initialize" { reply["result"] = [:] as [String: Any] }
            else if method == "account/rateLimits/read" {
                reply["result"] = ["rateLimits": ["primary": ["usedPercent": 25, "windowDurationMins": 300]]]
            } else { reply["error"] = ["code": -32601, "message": "Synthetic unsupported method"] }
            var output = Data("not-json\n".utf8)
            output.append(try! JSONSerialization.data(withJSONObject: reply)); output.append(10)
            try? FileHandle.standardOutput.write(contentsOf: output)
        }
        exit(0)
    }
    static func rpc() async throws {
        let server = AppServer(arguments: ["--mock-app-server"])
        defer { server.stop() }
        try await server.start(custom: Bundle.main.executableURL!.path)
        let limits = try await server.request("account/rateLimits/read")
        try check(UsageParser.official(limits).first?.percent == 75, "RPC handshake and malformed-line recovery")
        var rejected = false
        do { _ = try await server.request("unsupported") } catch { rejected = true }
        try check(rejected, "RPC errors must reject")
        let waiting = Task { try await server.request("never-reply") }
        try await Task.sleep(nanoseconds: 100_000_000)
        server.stop()
        let stopped = await waiting.result
        switch stopped {
        case .success: throw UsageError.message("Stopped request unexpectedly succeeded")
        case .failure: break
        }
        try check(!server.alive, "RPC child cleanup")
    }
    static func database() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("CodexBarSelfTest-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let path = dir.appendingPathComponent("cc-switch.db")
        var db: OpaquePointer?
        guard sqlite3_open(path.path, &db) == SQLITE_OK else { throw UsageError.message("Fixture database create") }
        defer { sqlite3_close(db) }
        let sql = """
        CREATE TABLE providers(id TEXT, name TEXT, category TEXT, meta TEXT, settings_config TEXT, app_type TEXT, is_current INTEGER);
        INSERT INTO providers VALUES('a','First','official','{}','{}','codex',1);
        INSERT INTO providers VALUES('b','Selected','','{"usage_script":{"enabled":true,"code":"({})","autoQueryInterval":0}}','{}','codex',0);
        """
        guard sqlite3_exec(db, sql, nil, nil, nil) == SQLITE_OK else { throw UsageError.message("Fixture database schema") }
        try Data(#"{"currentProviderCodex":"b"}"#.utf8).write(to: dir.appendingPathComponent("settings.json"))
        let before = try Data(contentsOf: path)
        let selected = try CCSwitch.current(dir.path, required: true)
        try check(selected?.id == "b" && selected?.interval == 0 && selected?.enabled == true, "settings selection and manual interval")
        try Data(#"{"currentProviderCodex":"missing"}"#.utf8).write(to: dir.appendingPathComponent("settings.json"))
        let fallback = try CCSwitch.current(dir.path, required: true)
        try check(fallback?.id == "a" && fallback?.official == true, "is_current fallback")
        let after = try Data(contentsOf: path)
        try check(before == after, "Database must remain unchanged")
    }
    static func template() async throws {
        guard let url = Bundle.main.url(forResource: "package-quota", withExtension: "js") else { throw UsageError.message("Packaged JS template missing") }
        var provider = Provider(); provider.code = try String(contentsOf: url, encoding: .utf8)
        provider.baseUrl = "https://example.invalid"; provider.accessToken = "synthetic-token"
        let request = try await ScriptRunner.request(provider)
        try check(request["url"] as? String == "https://example.invalid/api/user/self/package-quota", "Template placeholders")
        let now = Date(), iso = ISO8601DateFormatter()
        let fixture: [String: Any] = ["success": true, "data": ["plan": "pro", "updated_at": iso.string(from: now), "status": "available",
            "site_balance": ["status": "available", "remaining": 0],
            "windows": [["window_seconds": 604800, "remaining_percent": 72, "reset_at": iso.string(from: now.addingTimeInterval(86400)), "name": "week"]]]]
        let rows = try await ScriptRunner.extract(provider, response: JSONSerialization.data(withJSONObject: fixture))
        try check(rows.contains { $0.kind == "quota" && $0.percent == 72 && $0.reset != nil }, "PRO weekly quota")
        try check(rows.contains { $0.kind == "balance" && $0.remaining == 0 && $0.valid }, "Zero balance remains visible")
    }
    @MainActor static func render() throws {
        _ = NSApplication.shared
        let c = Controller(); c.connected = true; c.thirdParty = true
        let directory = ProcessInfo.processInfo.environment["CODEXBAR_TEST_OUTPUT"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.temporaryDirectory.appendingPathComponent("CodexBar-Previews")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        for dark in [false, true] {
            for chinese in [false, true] {
                c.preferences.language = chinese ? "zh" : "en"
                c.theme.background = CodexTheme.color(dark ? "#171717" : "#f6f6f6")
                c.theme.foreground = CodexTheme.color(dark ? "#fefefe" : "#030303")
                c.rows = [UsageRow(id: "q", name: "Week", remaining: 72, percent: 72, reset: Date().addingTimeInterval(86400)),
                          UsageRow(id: "b", name: "Balance", kind: "balance", remaining: 0, unit: "USD"),
                          UsageRow(id: "e", name: "Estimate", kind: "estimate", remaining: 0)]
                let panel = OverlayPanel(controller: c)
                panel.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
                for expanded in [false, true] {
                    panel.expanded = expanded
                    guard let view = panel.contentView, let bitmap = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw UsageError.message("UI bitmap allocation") }
                    view.cacheDisplay(in: view.bounds, to: bitmap)
                    guard let data = bitmap.representation(using: .png, properties: [:]) else { throw UsageError.message("UI PNG encode") }
                    let name = "\(dark ? "dark" : "light")-\(chinese ? "zh" : "en")-\(expanded ? "expanded" : "collapsed").png"
                    try data.write(to: directory.appendingPathComponent(name))
                }
                panel.close()
            }
        }
    }
}
