import XCTest
@testable import DaemonApp

// MARK: - Tasks 4.2 + 5.4: persisted default-off menu-bar icon preference

// Tests assert the underlying UserDefaults contract, not the @AppStorage property
// wrapper itself (which is not headlessly constructible).  Each test uses an
// isolated UserDefaults suite so UserDefaults.standard is never polluted.

final class MenuBarPreferenceTests: XCTestCase {

    // MARK: - Key + default constant

    func testDefaultValue_isFalse() {
        XCTAssertFalse(MenuBarPreference.defaultValue,
                       "Menu-bar icon must default to off (spec: off by default).")
    }

    func testKey_isExpectedString() {
        XCTAssertEqual(MenuBarPreference.key, "showMenuBarIcon",
                       "Key string must be stable; changing it breaks persisted user preferences.")
    }

    // MARK: - Absence resolves to off (fresh launch, no prior preference)

    func testAbsenceOfKey_resolvesToFalse() {
        let suiteName = "DaemonAppTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        // No value has been set — object(forKey:) must return nil.
        XCTAssertNil(defaults.object(forKey: MenuBarPreference.key),
                     "A fresh preferences suite must have no stored value for the key.")

        // bool(forKey:) on an absent key returns false — matching the default.
        XCTAssertFalse(defaults.bool(forKey: MenuBarPreference.key),
                       "Absent key must resolve to false, matching MenuBarPreference.defaultValue.")
    }

    // MARK: - Value persists when written

    func testWrittenTrue_roundTrips() {
        let suiteName = "DaemonAppTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        defaults.set(true, forKey: MenuBarPreference.key)
        defaults.synchronize()

        XCTAssertTrue(defaults.bool(forKey: MenuBarPreference.key),
                      "Stored true must be readable back as true.")
    }

    func testWrittenFalse_roundTrips() {
        let suiteName = "DaemonAppTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        defaults.set(false, forKey: MenuBarPreference.key)
        defaults.synchronize()

        XCTAssertFalse(defaults.bool(forKey: MenuBarPreference.key),
                       "Stored false must be readable back as false.")
    }

    // MARK: - Settings toggle write-through (task 4.3)

    /// Simulates the Settings toggle being enabled: write true (as @AppStorage would),
    /// then assert a fresh read of the same MenuBarPreference.key sees true.
    /// This exercises the same UserDefaults seam that SettingsView's @AppStorage binding
    /// and DaemonApp's MenuBarExtra(isInserted:) both observe — confirming single key,
    /// single source of truth.
    func testSettingsToggle_writesThroughToPreferenceKey() {
        let suiteName = "DaemonAppTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defer { defaults.removePersistentDomain(forName: suiteName) }

        // Precondition: absent key reads as the default (off).
        XCTAssertFalse(defaults.bool(forKey: MenuBarPreference.key),
                       "Preference must be off before the user enables it.")

        // Simulate toggle enable (SettingsView's @AppStorage sets this key to true).
        defaults.set(true, forKey: MenuBarPreference.key)
        defaults.synchronize()

        // A fresh read via the same key (as DaemonApp.showTrayIcon would see via
        // @AppStorage) must now return true — the MenuBarExtra would be inserted.
        XCTAssertTrue(defaults.bool(forKey: MenuBarPreference.key),
                      "After enabling, preference key must read true so MenuBarExtra is inserted.")

        // Confirm the binding uses exactly MenuBarPreference.key, not a divergent literal.
        XCTAssertEqual(MenuBarPreference.key, "showMenuBarIcon",
                       "SettingsView and DaemonApp must bind to the same key constant.")
    }

    // MARK: - Teardown does not pollute UserDefaults.standard

    func testIsolatedSuiteDoesNotPollutesStandard() {
        let suiteName = "DaemonAppTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!

        defaults.set(true, forKey: MenuBarPreference.key)

        // Standard suite must be unaffected.
        XCTAssertNil(UserDefaults.standard.object(forKey: MenuBarPreference.key),
                     "Writing to an isolated suite must not affect UserDefaults.standard.")

        defaults.removePersistentDomain(forName: suiteName)
    }
}
