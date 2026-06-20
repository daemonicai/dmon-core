import XCTest
@testable import DaemonApp

// MARK: - Task 8.4: ConfigStore flat-YAML round-trip and secret-omission rules

// All FS work uses a per-test temp dir; no writes to ~/.dmon/config.yaml.
final class ConfigStoreTests: XCTestCase {

    private var tempDir: URL!
    private var configURL: URL!

    override func setUp() {
        super.setUp()
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        try? FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        configURL = tempDir.appendingPathComponent("config.yaml")
    }

    override func tearDown() {
        if let dir = tempDir {
            try? FileManager.default.removeItem(at: dir)
        }
        tempDir = nil
        configURL = nil
        super.tearDown()
    }

    // MARK: - Round-trip: plaintext key survives save+load

    func testRoundTrip_plaintextKey_survives() {
        ConfigStore.save(
            plaintext: ["DMON_E2B_URL": "http://x"],
            secrets: [:],
            keychainAvailable: true,
            at: configURL
        )
        let loaded = ConfigStore.load(at: configURL)
        XCTAssertEqual(loaded["DMON_E2B_URL"], "http://x")
    }

    // MARK: - Round-trip: URL value with multiple colons splits on first colon only

    func testRoundTrip_urlValue_firstColonSplitOnly() {
        ConfigStore.save(
            plaintext: ["DMON_E2B_URL": "http://localhost:8080/v1"],
            secrets: [:],
            keychainAvailable: true,
            at: configURL
        )
        let loaded = ConfigStore.load(at: configURL)
        XCTAssertEqual(
            loaded["DMON_E2B_URL"],
            "http://localhost:8080/v1",
            "URL value with multiple colons must round-trip intact (first-colon split)"
        )
    }

    // MARK: - Comment and blank lines are skipped on load

    func testLoad_skipsCommentsAndBlanks() throws {
        let yaml = """
        # Managed by Daemon.App settings panel.
        DMON_E2B_URL: http://x

        # another comment
        DMON_E2B_MODEL: gemma4
        """
        try yaml.write(to: configURL, atomically: true, encoding: .utf8)
        let loaded = ConfigStore.load(at: configURL)
        XCTAssertEqual(loaded["DMON_E2B_URL"], "http://x")
        XCTAssertEqual(loaded["DMON_E2B_MODEL"], "gemma4")
        // Comment lines must not appear as keys.
        XCTAssertNil(loaded["# Managed by Daemon.App settings panel."])
        XCTAssertNil(loaded["# another comment"])
    }

    // MARK: - Secret omission: empty-value secret must be absent after save+load

    func testSave_emptySecret_isOmitted() {
        ConfigStore.save(
            plaintext: [:],
            secrets: ["GEMINI_API_KEY": ""],
            keychainAvailable: true,
            at: configURL
        )
        let loaded = ConfigStore.load(at: configURL)
        XCTAssertNil(loaded["GEMINI_API_KEY"], "Empty-value secret must not appear in the written file")
    }

    // MARK: - Keychain token: keychainAvailable=true writes literal "keychain", not the real value

    func testSave_secretWithKeychainAvailable_writesKeychainToken() {
        ConfigStore.save(
            plaintext: [:],
            secrets: ["GEMINI_API_KEY": "real-secret-value"],
            keychainAvailable: true,
            at: configURL
        )
        let loaded = ConfigStore.load(at: configURL)
        XCTAssertEqual(
            loaded["GEMINI_API_KEY"],
            "keychain",
            "keychainAvailable=true must write the literal token 'keychain', not the real value"
        )
    }

    // MARK: - Unsigned-dev fallback: keychainAvailable=false writes the real value

    func testSave_secretWithKeychainUnavailable_writesRealValue() {
        ConfigStore.save(
            plaintext: [:],
            secrets: ["GEMINI_API_KEY": "real-secret-value"],
            keychainAvailable: false,
            at: configURL
        )
        let loaded = ConfigStore.load(at: configURL)
        XCTAssertEqual(
            loaded["GEMINI_API_KEY"],
            "real-secret-value",
            "keychainAvailable=false must write the plaintext secret value for unsigned-dev builds"
        )
    }

    // MARK: - DMON_ key env mapping survives round-trip

    func testRoundTrip_dmonKeys_survive() {
        let plaintext: [String: String] = [
            "DMON_E2B_URL":      "http://e2b",
            "DMON_REASONER_URL": "http://reasoner",
            "DMON_E2B_MODEL":    "gemma4:e2b-it-qat",
        ]
        ConfigStore.save(
            plaintext: plaintext,
            secrets: [:],
            keychainAvailable: true,
            at: configURL
        )
        let loaded = ConfigStore.load(at: configURL)
        for (key, value) in plaintext {
            XCTAssertEqual(loaded[key], value, "Key '\(key)' must survive round-trip")
        }
    }

    // MARK: - File missing: load returns empty dictionary (no crash)

    func testLoad_missingFile_returnsEmpty() {
        let missing = tempDir.appendingPathComponent("no-such-file.yaml")
        let loaded = ConfigStore.load(at: missing)
        XCTAssertTrue(loaded.isEmpty)
    }
}
