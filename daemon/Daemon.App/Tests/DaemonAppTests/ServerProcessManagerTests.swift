import XCTest
@testable import DaemonApp

// MARK: - Task 8.2: ServerProcessManager PID adoption via injected liveness probe

// ServerProcessManager is @MainActor; all test methods must run on the main actor.
@MainActor
final class ServerProcessManagerTests: XCTestCase {

    private var tempDir: URL!

    override func setUp() async throws {
        try await super.setUp()
        tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
    }

    override func tearDown() async throws {
        if let dir = tempDir {
            try? FileManager.default.removeItem(at: dir)
        }
        tempDir = nil
        try await super.tearDown()
    }

    // MARK: - Helpers

    private func makeManager(
        pidFileURL: URL,
        probe: @escaping PidLivenessProbe
    ) -> ServerProcessManager {
        let config = ServerProcessConfig(
            displayName: "TestServer",
            executableCandidates: [],
            arguments: [],
            pidFileURL: pidFileURL
        )
        return ServerProcessManager(config: config, livenessProbe: probe)
    }

    private func writePID(_ pid: String, to url: URL) throws {
        try Data(pid.utf8).write(to: url)
    }

    // MARK: - Tests

    func testAdoptIfRunning_liveProcess_returnsTrue() throws {
        let pidURL = tempDir.appendingPathComponent("server.pid")
        try writePID("12345", to: pidURL)

        let manager = makeManager(pidFileURL: pidURL, probe: { _ in true })
        XCTAssertTrue(manager.adoptIfRunning())
    }

    func testAdoptIfRunning_deadProcess_returnsFalse() throws {
        let pidURL = tempDir.appendingPathComponent("server.pid")
        try writePID("12345", to: pidURL)

        let manager = makeManager(pidFileURL: pidURL, probe: { _ in false })
        XCTAssertFalse(manager.adoptIfRunning())
    }

    func testAdoptIfRunning_noPidFile_returnsFalse() {
        // Non-existent path — probe must never be consulted.
        let pidURL = tempDir.appendingPathComponent("nonexistent.pid")
        var probeInvoked = false
        let manager = makeManager(pidFileURL: pidURL, probe: { _ in
            probeInvoked = true
            return true
        })
        XCTAssertFalse(manager.adoptIfRunning())
        XCTAssertFalse(probeInvoked, "Probe must not be called when no PID file exists")
    }

    func testAdoptIfRunning_malformedPid_returnsFalse() throws {
        // File exists but content is not a valid integer.
        let pidURL = tempDir.appendingPathComponent("bad.pid")
        try writePID("not-a-pid", to: pidURL)

        var probeInvoked = false
        let manager = makeManager(pidFileURL: pidURL, probe: { _ in
            probeInvoked = true
            return true
        })
        XCTAssertFalse(manager.adoptIfRunning())
        XCTAssertFalse(probeInvoked, "Probe must not be called when PID cannot be parsed")
    }
}
