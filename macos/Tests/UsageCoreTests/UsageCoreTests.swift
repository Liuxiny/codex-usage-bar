import XCTest
@testable import UsageCore

final class UsageCoreTests: XCTestCase {
    func testOfficialPrefersModernBucketsAndClamps() {
        let response: [String: Any] = [
            "rateLimits": ["primary": ["usedPercent": 99]],
            "rateLimitsByLimitId": ["codex": ["primary": ["usedPercent": 25, "windowDurationMins": 300],
                                                  "secondary": ["usedPercent": 110, "windowDurationMins": 10080]]]
        ]
        let rows = UsageParser.official(response)
        XCTAssertEqual(rows.count, 2); XCTAssertEqual(rows[0].remaining, 75)
        XCTAssertEqual(rows[1].remaining, 0); XCTAssertEqual(rows[1].name, "Week")
    }
    func testNullModernFallsBackAndUnknownIsNotZero() {
        XCTAssertEqual(UsageParser.official(["rateLimitsByLimitId": NSNull(), "rateLimits": ["primary": ["usedPercent": 40]]]).first?.percent, 60)
        XCTAssertTrue(UsageParser.official(["rateLimits": ["primary": ["usedPercent": NSNull()]]]).isEmpty)
        XCTAssertNil(number(true)); XCTAssertNil(number(Double.infinity))
        XCTAssertEqual(UsageFormat.compact(nil), "—")
    }
    func testTokensUseUTCYesterday() {
        let now = ISO8601DateFormatter().date(from: "2026-09-10T01:00:00Z")!
        let result = UsageParser.tokens(["summary": ["lifetimeTokens": 9876], "dailyUsageBuckets": [
            ["startDate": "2026-09-09", "tokens": 1234], ["startDate": "2026-09-10", "tokens": 42]
        ]], now: now)
        XCTAssertEqual(result.0, 1234); XCTAssertEqual(result.1, 9876)
        XCTAssertEqual(UsageParser.tokens(["dailyUsageBuckets": []], now: now).0, 0)
        XCTAssertNil(UsageParser.tokens([:]).0)
    }
    func testCompactRoundingAndCarry() {
        for (input, expected) in [(999.0, "999"), (1000, "1K"), (1250, "1.3K"), (999949, "999.9K"), (999950, "1M"), (1e9, "1B")] {
            XCTAssertEqual(UsageFormat.compact(input), expected)
        }
    }
    func testThirdPartyZeroEstimateAndPackageMetadata() {
        let result: [[String: Any]] = [
            ["planName": "PRO · 周", "remaining": 72, "total": 100, "unit": "%", "isValid": true],
            ["planName": "PRO · 站内余额", "remaining": 0, "unit": "USD", "isValid": true],
            ["planName": "PRO · 余额折合周额度", "remaining": 0, "unit": "%", "isValid": true]
        ]
        let rows = UsageParser.thirdParty(result, response: ["success": true, "data": ["plan": "pro", "windows": [
            ["window_seconds": 604800, "reset_at": "2026-09-12T10:00:00Z"]
        ]]])
        XCTAssertEqual(rows.map(\.kind), ["quota", "balance", "estimate"])
        XCTAssertNotNil(rows[0].reset); XCTAssertEqual(rows[1].remaining, 0); XCTAssertTrue(rows[2].valid)
    }
    func testInvalidRowsAndUnknownResetStayUnknown() {
        let rows = UsageParser.thirdParty(["planName": "Weekly", "isValid": false, "invalidMessage": "expired", "extra": "Tomorrow 10:00"])
        XCTAssertFalse(rows[0].valid); XCTAssertNil(rows[0].remaining); XCTAssertNil(rows[0].reset)
        XCTAssertEqual(UsageFormat.reset(nil, chinese: false), "Reset unknown")
        XCTAssertEqual(UsageParser.thirdParty(Array(repeating: ["remaining": 1], count: 40)).count, 32)
    }
    func testActiveProviderOnlyAndLiteralComments() {
        let toml = """
        model_provider = "active"
        base_url = "https://fallback.example"
        [model_providers.other]
        experimental_bearer_token = "wrong"
        [model_providers.active]
        base_url = "https://right.example/#part" # comment
        """
        XCTAssertEqual(ScalarTOML.providerValue(toml, key: "base_url"), "https://right.example/#part")
        XCTAssertEqual(ScalarTOML.providerValue(toml, key: "experimental_bearer_token"), "")
        XCTAssertEqual(ScalarTOML.parse("[desktop]\nsansFontSize = 14\nappearanceTheme = 'dark'")["desktop.appearanceTheme"], "dark")
    }
    func testResetMatchesWindowsCompactAndExpandedRules() {
        let calendar = Calendar.current
        let now = calendar.date(from: DateComponents(year: 2026, month: 9, day: 10, hour: 9))!
        let today = calendar.date(from: DateComponents(year: 2026, month: 9, day: 10, hour: 16, minute: 30))!
        let nextYear = calendar.date(from: DateComponents(year: 2027, month: 1, day: 2, hour: 16, minute: 30))!
        XCTAssertEqual(UsageFormat.reset(today, chinese: true, now: now), "16:30")
        XCTAssertEqual(UsageFormat.reset(today, chinese: true, compact: false, now: now), "今天 16:30 重置")
        XCTAssertEqual(UsageFormat.reset(nextYear, chinese: false, now: now), "2027/1/2")
        XCTAssertEqual(UsageFormat.reset(nextYear, chinese: false, compact: false, now: now), "2027/1/2 16:30 reset")
    }
}
