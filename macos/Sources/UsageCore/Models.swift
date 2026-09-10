import Foundation
import CoreFoundation

public enum UsageError: Error, LocalizedError {
    case message(String)
    public var errorDescription: String? { if case .message(let text) = self { return text }; return nil }
}

public func number(_ value: Any?) -> Double? {
    guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { return nil }
    return n.doubleValue
}

public struct UsageRow: Identifiable {
    public var id: String
    public var name: String
    public var kind: String
    public var remaining: Double?
    public var percent: Double?
    public var reset: Date?
    public var unit: String
    public var valid: Bool
    public var detail: String
    public init(id: String, name: String, kind: String = "quota", remaining: Double? = nil,
                percent: Double? = nil, reset: Date? = nil, unit: String = "%", valid: Bool = true, detail: String = "") {
        self.id = id; self.name = name; self.kind = kind; self.remaining = remaining
        self.percent = percent; self.reset = reset; self.unit = unit; self.valid = valid; self.detail = detail
    }
}

public enum UsageParser {
    public static func reset(_ value: Any?) -> Date? {
        if let seconds = number(value), seconds > 0 { return Date(timeIntervalSince1970: seconds) }
        guard let text = value as? String else { return nil }
        let parser = ISO8601DateFormatter()
        parser.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return parser.date(from: text) ?? ISO8601DateFormatter().date(from: text)
    }

    public static func official(_ response: [String: Any]) -> [UsageRow] {
        var rows: [UsageRow] = []
        var seen = Set<String>()
        func visit(_ value: Any, path: String, depth: Int) {
            guard depth < 12 else { return }
            if let map = value as? [String: Any] {
                if let used = number(map["usedPercent"]) {
                    let duration = number(map["windowDurationMins"])
                    let date = reset(map["resetsAt"])
                    let identity = "\(used)|\(duration ?? -1)|\(date?.timeIntervalSince1970 ?? -1)"
                    guard seen.insert(identity).inserted else { return }
                    let key = path.components(separatedBy: ".").last ?? "Codex"
                    let label = duration == 300 || (duration == nil && key == "primary") ? "5h" : duration == 10080 || (duration == nil && key == "secondary") ? "Week" : key
                    let remaining = min(100, max(0, 100 - used))
                    rows.append(UsageRow(id: path, name: label, remaining: remaining, percent: remaining, reset: date))
                } else {
                    for key in map.keys.sorted() where key != "rateLimitResetCredits" {
                        visit(map[key]!, path: path + "." + key, depth: depth + 1)
                    }
                }
            } else if let array = value as? [Any] {
                for (i, entry) in array.enumerated() { visit(entry, path: "\(path).\(i)", depth: depth + 1) }
            }
        }
        let modern = response["rateLimitsByLimitId"]
        let root = modern is NSNull || modern == nil ? response["rateLimits"] ?? response : modern!
        visit(root, path: "limits", depth: 0)
        return Array(rows.prefix(32))
    }

    public static func tokens(_ response: [String: Any], now: Date = Date()) -> (Double?, Double?) {
        let lifetime = number((response["summary"] as? [String: Any])?["lifetimeTokens"])
        guard let buckets = response["dailyUsageBuckets"] as? [[String: Any]] else { return (nil, lifetime) }
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(secondsFromGMT: 0); f.dateFormat = "yyyy-MM-dd"
        let yesterday = f.string(from: now.addingTimeInterval(-86400))
        let count = buckets.first { $0["startDate"] as? String == yesterday }.flatMap { number($0["tokens"]) } ?? 0
        return (max(0, count), lifetime.flatMap { $0 >= 0 ? $0 : nil })
    }

    public static func thirdParty(_ result: Any, response: [String: Any] = [:]) -> [UsageRow] {
        let maps = (result as? [[String: Any]]) ?? (result as? [String: Any]).map { [$0] } ?? []
        let data = (response["data"] as? [String: Any]) ?? [:]
        let windows = data["windows"] as? [[String: Any]] ?? []
        let prefix = ((data["plan"] as? String) ?? "套餐").uppercased() + " · "
        return maps.prefix(32).enumerated().map { index, map in
            let name = String(((map["planName"] as? String) ?? "Usage").prefix(160))
            let unit = String(((map["unit"] as? String) ?? "").prefix(32))
            var kind = map["kind"] as? String ?? (unit == "% 周" ? "estimate" : ["USD", "CNY", "EUR"].contains(unit) ? "balance" : "usage")
            var date = reset(map["resetAt"])
            if response["success"] as? Bool == true {
                if name == prefix + "余额折合周额度" { kind = "estimate" }
                if name == prefix + "站内余额" { kind = "balance" }
                let seconds: Double? = name == prefix + "5小时" ? 18000 : name == prefix + "周" ? 604800 : nil
                if let seconds {
                    kind = "quota"
                    date = windows.first { number($0["window_seconds"]) == seconds }.flatMap { reset($0["reset_at"]) } ?? date
                }
            }
            let total = number(map["total"]), used = number(map["used"])
            let remaining = number(map["remaining"]) ?? total.flatMap { t in used.map { t - $0 } }
            let isQuota = kind != "estimate" && unit == "%" && total == 100 && remaining.map { $0 >= 0 && $0 <= 100 } == true
            if isQuota { kind = "quota" } else if kind == "quota" { kind = "usage" }
            let percent = isQuota ? remaining : nil
            let duration = number(map["windowSeconds"])
            let label = duration == 18000 ? "5h" : duration == 604800 ? "Week" : name
            return UsageRow(id: "\(index)", name: label, kind: kind, remaining: remaining,
                            percent: percent.map { min(100, max(0, $0)) }, reset: date, unit: unit,
                            valid: ((map["isValid"] as? Bool) ?? true) && (remaining != nil || total != nil || used != nil),
                            detail: String(((map["invalidMessage"] as? String) ?? (map["extra"] as? String) ?? "").prefix(500)))
        }
    }
}

public enum UsageFormat {
    public static func compact(_ value: Double?) -> String {
        guard let value, value.isFinite, value >= 0 else { return "—" }
        let units = ["", "K", "M", "B", "T"]
        var index = 0, scaled = value
        while scaled >= 999.95 && index < units.count - 1 { scaled /= 1000; index += 1 }
        let rounded = (scaled * 10).rounded() / 10
        return (rounded == rounded.rounded() ? String(format: "%.0f", rounded) : String(format: "%.1f", rounded)) + units[index]
    }
    public static func reset(_ date: Date?, chinese: Bool, compact: Bool = true, now: Date = Date()) -> String {
        guard let date else { return chinese ? "重置时间未知" : "Reset unknown" }
        let calendar = Calendar.current, f = DateFormatter()
        f.locale = Locale(identifier: chinese ? "zh_CN" : "en_US")
        var prefix = ""
        if calendar.isDate(date, inSameDayAs: now) { f.dateFormat = "HH:mm"; prefix = compact ? "" : chinese ? "今天 " : "Today " }
        else if let tomorrow = calendar.date(byAdding: .day, value: 1, to: now), calendar.isDate(date, inSameDayAs: tomorrow) {
            f.dateFormat = "HH:mm"; prefix = chinese ? "明天 " : "Tomorrow "
        } else if calendar.component(.year, from: date) == calendar.component(.year, from: now) {
            f.dateFormat = chinese ? "M月d日 HH:mm" : "M/d HH:mm"
        } else {
            f.dateFormat = chinese ? (compact ? "yyyy年M月d日" : "yyyy年M月d日 HH:mm") : (compact ? "yyyy/M/d" : "yyyy/M/d HH:mm")
        }
        return prefix + f.string(from: date) + (compact ? "" : chinese ? " 重置" : " reset")
    }
}

// Small scalar TOML reader: deliberately only accepts quoted values and numbers.
// Unsupported tables/arrays never become credentials from another provider.
public enum ScalarTOML {
    public static func parse(_ text: String) -> [String: String] {
        var result: [String: String] = [:], section = ""
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("["), let end = line.firstIndex(of: "]") {
                section = String(line[line.index(after: line.startIndex)..<end]).replacingOccurrences(of: "\"", with: "").replacingOccurrences(of: "'", with: "")
                continue
            }
            guard let equal = line.firstIndex(of: "="), !line.hasPrefix("#") else { continue }
            let key = line[..<equal].trimmingCharacters(in: .whitespaces).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
            let value = line[line.index(after: equal)...].trimmingCharacters(in: .whitespaces)
            var parsed: String?
            if value.hasPrefix("\"") {
                var escaped = false
                for i in value.indices.dropFirst() {
                    if value[i] == "\"" && !escaped {
                        parsed = (try? JSONSerialization.jsonObject(with: Data(value[...i].utf8), options: .fragmentsAllowed)) as? String
                        break
                    }
                    if value[i] == "\\" { escaped.toggle() } else { escaped = false }
                }
            } else if value.hasPrefix("'"), let end = value.dropFirst().firstIndex(of: "'") {
                parsed = String(value[value.index(after: value.startIndex)..<end])
            } else {
                let scalar = value.components(separatedBy: "#")[0].trimmingCharacters(in: .whitespaces)
                if Double(scalar) != nil { parsed = scalar }
            }
            if let parsed { result[section.isEmpty ? key : section + "." + key] = parsed }
        }
        return result
    }
    public static func providerValue(_ text: String, key: String) -> String {
        let values = parse(text)
        if let active = values["model_provider"], let value = values["model_providers.\(active).\(key)"] { return value }
        return values[key] ?? ""
    }
}
