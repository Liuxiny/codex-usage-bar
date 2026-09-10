import AppKit
import Security
import UsageCore

struct Provider: Codable, Equatable {
    var id = "standalone"
    var name = "Custom API"
    var official = false
    var enabled = true
    var code = ""
    var baseUrl = ""
    var apiKey = ""
    var accessToken = ""
    var userId = ""
    var interval = 2
    var timeout = 10
}

struct Preferences: Codable {
    var source = "auto"
    var ccDirectory = ""
    var cliPath = ""
    var language = "system"
    var follow = true
    var visible = true
    var x: Double?
    var y: Double?
    var chinese: Bool { language == "zh" || (language == "system" && (Locale.preferredLanguages.first ?? "en").hasPrefix("zh")) }
    static let directory = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/CodexUsageBar", isDirectory: true)
    static let file = directory.appendingPathComponent("settings.json")
    static func load() -> Preferences { (try? JSONDecoder().decode(Self.self, from: Data(contentsOf: file))) ?? Self() }
    func save() throws {
        try FileManager.default.createDirectory(at: Self.directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try JSONEncoder().encode(self).write(to: Self.file, options: .atomic)
    }
}

enum CredentialStore {
    static let service = "com.codexusagebar.macos"
    static let account = "standalone-provider"
    static func query() -> [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: account]
    }
    static func load() throws -> Provider {
        var q = query(); q[kSecReturnData as String] = true; q[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(q as CFDictionary, &item)
        if status == errSecItemNotFound { return Provider() }
        guard status == errSecSuccess, let data = item as? Data else { throw UsageError.message("Keychain unavailable (\(status))") }
        return try JSONDecoder().decode(Provider.self, from: data)
    }
    static func save(_ provider: Provider) throws {
        let data = try JSONEncoder().encode(provider)
        let update = [kSecValueData as String: data]
        var status = SecItemUpdate(query() as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var q = query(); q[kSecValueData as String] = data
            q[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            status = SecItemAdd(q as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw UsageError.message("Keychain save failed (\(status))") }
    }
}

@MainActor struct CodexTheme {
    var appearance: NSAppearance?
    var accent = NSColor(srgbRed: 1, green: 99.0 / 255, blue: 99.0 / 255, alpha: 1)
    var background: NSColor?
    var foreground: NSColor?
    var fontName = ""
    var fontSize: CGFloat = 13
    static func load() -> Self {
        let home = ProcessInfo.processInfo.environment["CODEX_HOME"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        let text = (try? String(contentsOf: home.appendingPathComponent("config.toml"), encoding: .utf8)) ?? ""
        let values = ScalarTOML.parse(text)
        var theme = Self()
        let mode = values["desktop.appearanceTheme"] ?? "system"
        if mode == "dark" { theme.appearance = NSAppearance(named: .darkAqua) }
        if mode == "light" { theme.appearance = NSAppearance(named: .aqua) }
        let dark = mode == "dark" || (mode != "light" && NSApp.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua)
        let prefix = dark ? "desktop.appearanceDarkChromeTheme." : "desktop.appearanceLightChromeTheme."
        theme.background = color(values[prefix + "surface"] ?? (dark ? "#171717" : "#f6f6f6"))
        theme.foreground = color(values[prefix + "ink"] ?? (dark ? "#fefefe" : "#030303"))
        theme.accent = color(values[prefix + "accent"] ?? "#ff6363") ?? theme.accent
        theme.fontName = values[prefix + "fonts.uiFace.postscriptName"] ?? values[prefix + "fonts.uiFace.fullName"] ?? values[prefix + "fonts.ui"]?.components(separatedBy: ",").first?.trimmingCharacters(in: CharacterSet(charactersIn: " \"'")) ?? ""
        theme.fontSize = CGFloat(min(16, max(11, Double(values["desktop.sansFontSize"] ?? "13") ?? 13)))
        return theme
    }
    static func color(_ text: String) -> NSColor? {
        var hex = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if hex.hasPrefix("#") {
            hex.removeFirst()
            if hex.count == 3 || hex.count == 4 { hex = hex.map { "\($0)\($0)" }.joined() }
            guard (hex.count == 6 || hex.count == 8), let n = UInt64(hex, radix: 16) else { return nil }
            let alpha = hex.count == 8 ? CGFloat(n & 255) / 255 : 1
            let rgb = hex.count == 8 ? n >> 8 : n
            return NSColor(srgbRed: CGFloat((rgb >> 16) & 255) / 255, green: CGFloat((rgb >> 8) & 255) / 255, blue: CGFloat(rgb & 255) / 255, alpha: alpha)
        }
        if hex.hasPrefix("rgb"), let open = hex.firstIndex(of: "("), let close = hex.lastIndex(of: ")") {
            let parts = hex[hex.index(after: open)..<close].split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            guard parts.count == 3 || parts.count == 4 else { return nil }
            func channel(_ s: String) -> CGFloat? {
                let percent = s.hasSuffix("%")
                guard let value = Double(percent ? String(s.dropLast()) : s), value.isFinite else { return nil }
                return CGFloat(min(1, max(0, value / (percent ? 100 : 255))))
            }
            guard let r = channel(parts[0]), let g = channel(parts[1]), let b = channel(parts[2]) else { return nil }
            let alpha = parts.count == 4 ? Double(parts[3]) ?? 1 : 1
            return NSColor(srgbRed: r, green: g, blue: b, alpha: CGFloat(min(1, max(0, alpha))))
        }
        return nil
    }
}
