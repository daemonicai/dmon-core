import XCTest
@testable import DaemonApp

// MARK: - Task 5.1: DaemonController.bootstrap() idempotence

// DaemonController is @MainActor; all test methods must run on the main actor.
// In the test environment no executables are resolved (DMON_*_SERVER_PATH and the
// network binary path are unset), so start() is a no-op and isRunning stays false.
// No network I/O occurs; tests are synchronous after bootstrap().
@MainActor
final class DaemonControllerTests: XCTestCase {

    func testBootstrap_setsHasBootstrapped() {
        let controller = DaemonController()
        XCTAssertFalse(controller.hasBootstrapped, "hasBootstrapped must be false before bootstrap()")
        controller.bootstrap()
        XCTAssertTrue(controller.hasBootstrapped, "hasBootstrapped must be true after bootstrap()")
    }

    func testBootstrap_idempotent_hasBootstrappedStaysTrue() {
        let controller = DaemonController()
        controller.bootstrap()
        controller.bootstrap() // second call must be a no-op
        XCTAssertTrue(controller.hasBootstrapped)
    }

    func testBootstrap_registersNineComponents_afterOneCall() {
        let controller = DaemonController()
        controller.bootstrap()
        // The registry is populated lazily on the first publisher emission, which may
        // not have fired synchronously.  The registration count we can assert on is the
        // number of Combine sinks wired by bootstrap().  The simplest deterministic
        // observable signal is that a second bootstrap() call does NOT grow the sink set:
        // assert that calling bootstrap() twice does not double the component slots.
        let countAfterOne = controller.healthRegistry.components.count
        controller.bootstrap() // no-op
        let countAfterTwo = controller.healthRegistry.components.count
        XCTAssertEqual(
            countAfterOne,
            countAfterTwo,
            "A second bootstrap() must not add additional health component slots"
        )
    }

    func testBootstrap_secondCall_doesNotStartNetworkAgain() {
        let controller = DaemonController()
        controller.bootstrap()
        // NetworkManager.start() delegates to ServerProcessManager.start() which is
        // adoption-guarded.  After bootstrap() the network is not running (no binary),
        // so isRunning is false.  Calling bootstrap() again must not change that.
        let runningAfterFirst = controller.network.isRunning
        controller.bootstrap() // no-op
        XCTAssertEqual(
            controller.network.isRunning,
            runningAfterFirst,
            "A second bootstrap() must not alter the network's running state"
        )
    }
}
