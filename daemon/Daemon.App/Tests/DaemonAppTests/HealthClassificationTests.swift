import XCTest
@testable import DaemonApp

// MARK: - Task 8.3: Health classification + HealthRegistry rollup

// The free-function classifiers (tailscaleHealth, processHealth, endpointHealth,
// dmailHealth) are non-isolated; those test methods need no @MainActor annotation.
// HealthRegistry.rollup is a static method on a @MainActor class, so it IS
// @MainActor-isolated — the rollup test methods must be on the main actor.
final class HealthClassificationTests: XCTestCase {

    // MARK: - tailscaleHealth (non-isolated free function — no @MainActor needed)

    func testTailscaleHealth_up_isOk() {
        XCTAssertEqual(tailscaleHealth(status: .up), .ok)
    }

    func testTailscaleHealth_degraded_isDegraded() {
        XCTAssertEqual(tailscaleHealth(status: .degraded), .degraded)
    }

    func testTailscaleHealth_down_isDown() {
        XCTAssertEqual(tailscaleHealth(status: .down), .down)
    }

    // MARK: - processHealth

    func testProcessHealth_running_noExitCode_isOk() {
        XCTAssertEqual(processHealth(isRunning: true, lastExitCode: nil), .ok)
    }

    func testProcessHealth_running_withExitCode_isOk() {
        // Running overrides exit code (process was restarted).
        XCTAssertEqual(processHealth(isRunning: true, lastExitCode: 1), .ok)
    }

    func testProcessHealth_notRunning_noExitCode_isUnknown() {
        // Never launched — no exit code recorded.
        XCTAssertEqual(processHealth(isRunning: false, lastExitCode: nil), .unknown)
    }

    func testProcessHealth_notRunning_withExitCode_isDown() {
        XCTAssertEqual(processHealth(isRunning: false, lastExitCode: 1), .down)
    }

    // MARK: - endpointHealth

    func testEndpointHealth_didRespond_isOk() {
        // Any HTTP response (including 4xx/5xx) → ok.
        XCTAssertEqual(endpointHealth(didRespond: true), .ok)
    }

    func testEndpointHealth_noResponse_isDown() {
        XCTAssertEqual(endpointHealth(didRespond: false), .down)
    }

    // MARK: - dmailHealth
    // Contrast: Dmail requires 2xx (didSucceed); endpointHealth accepts any response.

    func testDmailHealth_succeeded_isOk() {
        XCTAssertEqual(dmailHealth(didSucceed: true), .ok)
    }

    func testDmailHealth_failed_isDown() {
        // Non-2xx or unreachable → down (NOT ok, unlike endpointHealth).
        XCTAssertEqual(dmailHealth(didSucceed: false), .down)
    }

    // Verify the rules are distinct: a non-2xx Dmail response is .down, but any
    // endpoint response (even 4xx) is .ok — the two free functions have opposite
    // semantics for the non-success case.
    func testDmailVsEndpoint_contrastingRules() {
        // endpointHealth(didRespond: true) → .ok  (any HTTP response, including 4xx/5xx)
        XCTAssertEqual(endpointHealth(didRespond: true), .ok)
        // dmailHealth(didSucceed: false) → .down (non-2xx Dmail response counts as failure)
        XCTAssertEqual(dmailHealth(didSucceed: false), .down)
    }

    // MARK: - HealthRegistry.rollup
    // rollup is static on a @MainActor class → must be called from @MainActor context.

    private func component(_ status: HealthStatus) -> ComponentHealth {
        ComponentHealth(name: "Test", status: status)
    }

    @MainActor
    func testRollup_gatewayStopped_alwaysRed() {
        // Decision 3: gatewayStopped → red regardless of component states.
        let result = HealthRegistry.rollup(
            gatewayStopped: true,
            components: [component(.ok), component(.ok)]
        )
        XCTAssertEqual(result, .red)
    }

    @MainActor
    func testRollup_gatewayRunning_withDown_isRed() {
        let result = HealthRegistry.rollup(
            gatewayStopped: false,
            components: [component(.ok), component(.down)]
        )
        XCTAssertEqual(result, .red)
    }

    @MainActor
    func testRollup_gatewayRunning_withDegraded_isAmber() {
        let result = HealthRegistry.rollup(
            gatewayStopped: false,
            components: [component(.ok), component(.degraded)]
        )
        XCTAssertEqual(result, .amber)
    }

    @MainActor
    func testRollup_gatewayRunning_withUnknown_isAmber() {
        let result = HealthRegistry.rollup(
            gatewayStopped: false,
            components: [component(.ok), component(.unknown)]
        )
        XCTAssertEqual(result, .amber)
    }

    @MainActor
    func testRollup_gatewayRunning_allOk_isGreen() {
        let result = HealthRegistry.rollup(
            gatewayStopped: false,
            components: [component(.ok), component(.ok)]
        )
        XCTAssertEqual(result, .green)
    }

    @MainActor
    func testRollup_empty_gatewayRunning_isGreen() {
        let result = HealthRegistry.rollup(gatewayStopped: false, components: [])
        XCTAssertEqual(result, .green)
    }

    // Decision 3: down must beat degraded — a mixed list with both must still be red.
    @MainActor
    func testRollup_downBeatsDegraded() {
        let result = HealthRegistry.rollup(
            gatewayStopped: false,
            components: [component(.degraded), component(.down), component(.ok)]
        )
        XCTAssertEqual(result, .red, "A .down component must produce .red even when .degraded is present")
    }

    // MARK: - ComponentHealth.lastUpdated (task 1.1 / 1.2)

    func testLastUpdated_seed_isNil() {
        // Seed / initial value: lastUpdated omitted → nil ("never published" semantic).
        let seed = component(.unknown)
        XCTAssertNil(seed.lastUpdated, "Seed ComponentHealth must have nil lastUpdated")
    }

    func testLastUpdated_stamped_isNonNil() {
        // A stamped snapshot (as produced at every publish site) must carry a non-nil date.
        let stamped = ComponentHealth(name: "Test", status: .ok, lastUpdated: Date())
        XCTAssertNotNil(stamped.lastUpdated, "Stamped ComponentHealth must have a non-nil lastUpdated")
    }

    func testLastUpdated_twoStamps_nonDecreasing() {
        // Two successive stamps must be non-decreasing (wall clock does not go backwards).
        let before = ComponentHealth(name: "Test", status: .ok, lastUpdated: Date())
        let after  = ComponentHealth(name: "Test", status: .ok, lastUpdated: Date())
        XCTAssertLessThanOrEqual(
            before.lastUpdated!, after.lastUpdated!,
            "Successive stamps must be non-decreasing"
        )
    }
}
