import Foundation
import CSQLite
import UsageCore

enum CCSwitch {
    static func directory(_ custom: String = "") throws -> URL {
        let env = ProcessInfo.processInfo.environment
        let explicit = custom.isEmpty ? env["CODEXBAR_CC_SWITCH_HOME"] ?? "" : custom
        if !explicit.isEmpty { return URL(fileURLWithPath: (explicit as NSString).expandingTildeInPath) }
        let home = FileManager.default.homeDirectoryForCurrentUser
        let store = home.appendingPathComponent("Library/Application Support/com.ccswitch.desktop/app_paths.json")
        if FileManager.default.fileExists(atPath: store.path) {
            let map = try JSONSerialization.jsonObject(with: Data(contentsOf: store)) as? [String: Any]
            if let path = map?["app_config_dir_override"] as? String, !path.isEmpty {
                return URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
            }
        }
        return home.appendingPathComponent(".cc-switch")
    }

    static func current(_ custom: String = "", required: Bool = false) throws -> Provider? {
        let directory = try directory(custom)
        let dbURL = directory.appendingPathComponent("cc-switch.db")
        guard FileManager.default.fileExists(atPath: dbURL.path) else {
            if required || !custom.isEmpty || ProcessInfo.processInfo.environment["CODEXBAR_CC_SWITCH_HOME"] != nil {
                throw UsageError.message("CC Switch: directory must contain cc-switch.db")
            }
            return nil
        }
        var db: OpaquePointer?
        let status = sqlite3_open_v2(dbURL.path, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, nil)
        defer { if let db { sqlite3_close(db) } }
        guard status == SQLITE_OK, let db else { throw UsageError.message("CC Switch database unavailable") }
        sqlite3_busy_timeout(db, 1000)
        var selected = ""
        let settings = directory.appendingPathComponent("settings.json")
        if FileManager.default.fileExists(atPath: settings.path) {
            let map = try JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any]
            selected = (map?["currentProviderCodex"] as? String) ?? ""
        }
        func read(_ id: String?) throws -> [String]? {
            var statement: OpaquePointer?
            let sql = "SELECT id,name,category,meta,settings_config FROM providers WHERE app_type='codex' AND " + (id == nil ? "is_current=1" : "id=?1") + " LIMIT 1"
            guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK else { throw UsageError.message("Unsupported CC Switch schema") }
            defer { sqlite3_finalize(statement) }
            if let id {
                let transient = unsafeBitCast(-1, to: sqlite3_destructor_type.self)
                guard sqlite3_bind_text(statement, 1, id, -1, transient) == SQLITE_OK else { throw UsageError.message("CC Switch selection failed") }
            }
            let step = sqlite3_step(statement)
            if step == SQLITE_DONE { return nil }
            guard step == SQLITE_ROW else { throw UsageError.message("CC Switch database busy") }
            return (0..<5).map { index in sqlite3_column_text(statement, Int32(index)).map { String(cString: $0) } ?? "" }
        }
        let preferred = selected.isEmpty ? nil : try read(selected)
        guard let row = try preferred ?? read(nil) else {
            if required { throw UsageError.message("No selected Codex provider in CC Switch") }
            return nil
        }
        func json(_ text: String) throws -> [String: Any] {
            if text.isEmpty { return [:] }
            return (try JSONSerialization.jsonObject(with: Data(text.utf8)) as? [String: Any]) ?? [:]
        }
        let meta = try json(row[3]), config = try json(row[4])
        let script = (meta["usage_script"] as? [String: Any]) ?? [:]
        let toml = (config["config"] as? String) ?? ""
        var p = Provider(); p.id = row[0]; p.name = row[1]
        p.official = row[2] == "official" || row[0] == "codex-official" || (meta["providerType"] as? String) == "codex_oauth"
        p.enabled = (script["enabled"] as? Bool) ?? false
        p.code = (script["code"] as? String) ?? ""
        p.baseUrl = (script["baseUrl"] as? String) ?? ""
        if p.baseUrl.isEmpty { p.baseUrl = ScalarTOML.providerValue(toml, key: "base_url") }
        while p.baseUrl.hasSuffix("/") { p.baseUrl.removeLast() }
        p.apiKey = (script["apiKey"] as? String) ?? ""
        if p.apiKey.isEmpty { p.apiKey = ((config["auth"] as? [String: Any])?["OPENAI_API_KEY"] as? String) ?? "" }
        if p.apiKey.isEmpty { p.apiKey = ScalarTOML.providerValue(toml, key: "experimental_bearer_token") }
        p.accessToken = (script["accessToken"] as? String) ?? ""
        p.userId = (script["userId"] as? String) ?? ""
        p.timeout = Int(min(30, max(2, number(script["timeout"]) ?? 10)))
        p.interval = Int(min(1440, max(0, number(script["autoQueryInterval"]) ?? 2)))
        return p
    }
}
