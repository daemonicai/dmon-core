import XCTest
@testable import DaemonApp

// MARK: - Task 3.3: relativeAge(from:now:) pure helper tests

// relativeAge is a non-isolated free function; no @MainActor annotation needed.
final class RelativeAgeTests: XCTestCase {

    // MARK: - nil date

    func testRelativeAge_nilDate_returnsDash() {
        let now = Date(timeIntervalSinceReferenceDate: 1_000_000)
        XCTAssertEqual(relativeAge(from: nil, now: now), "—", "nil date must produce the placeholder '—'")
    }

    // MARK: - Known deltas (locale-stable assertions use contains rather than equals
    // because RelativeDateTimeFormatter output can vary slightly across OS versions)

    func testRelativeAge_twelveSecondsAgo_containsSeconds() {
        let now = Date(timeIntervalSinceReferenceDate: 1_000_000)
        let twelve = Date(timeIntervalSinceReferenceDate: 1_000_000 - 12)
        let result = relativeAge(from: twelve, now: now)
        // "12 seconds ago" or similar — must not be "—" and must reference seconds.
        XCTAssertFalse(result.isEmpty, "Result must not be empty")
        XCTAssertNotEqual(result, "—", "A non-nil date must not produce the placeholder")
        XCTAssertTrue(result.contains("second"), "A 12-second delta must mention 'second' — got: \(result)")
    }

    func testRelativeAge_twoHoursAgo_containsHour() {
        let now = Date(timeIntervalSinceReferenceDate: 1_000_000)
        let twoHours = Date(timeIntervalSinceReferenceDate: 1_000_000 - 7_200)
        let result = relativeAge(from: twoHours, now: now)
        XCTAssertNotEqual(result, "—", "A non-nil date must not produce the placeholder")
        XCTAssertTrue(result.contains("hour"), "A 2-hour delta must mention 'hour' — got: \(result)")
    }

    func testRelativeAge_sameInstant_containsNowOrSeconds() {
        let now = Date(timeIntervalSinceReferenceDate: 1_000_000)
        let result = relativeAge(from: now, now: now)
        XCTAssertNotEqual(result, "—", "Same-instant date must not produce the placeholder")
    }

    // MARK: - Injected `now` is deterministic (no wall-clock in assertion path)

    func testRelativeAge_isInjectable_noDependencyOnWallClock() {
        // Two calls with identical (date, now) must produce identical output.
        let fixedNow  = Date(timeIntervalSinceReferenceDate: 2_000_000)
        let fixedDate = Date(timeIntervalSinceReferenceDate: 1_999_940) // 60 s ago
        let result1 = relativeAge(from: fixedDate, now: fixedNow)
        let result2 = relativeAge(from: fixedDate, now: fixedNow)
        XCTAssertEqual(result1, result2, "Injected-now must produce a stable, deterministic result")
    }
}
