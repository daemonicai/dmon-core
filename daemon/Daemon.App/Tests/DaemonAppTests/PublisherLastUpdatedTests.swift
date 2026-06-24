import XCTest
import Combine
@testable import DaemonApp

// MARK: - Task 5.3: each publisher stamps componentHealth.lastUpdated on publish
//
// All publisher classes are @MainActor, so every test method that touches one
// must be on the main actor.  The suite class is annotated @MainActor for that.
//
// Test strategy per publisher:
//   1. TailscaleMonitor  — synchronous construction-time stamp (no .receive(on:));
//                          init $status.map replay fires before the property read.
//   2. GatewayManager    — CombineLatest + .receive(on: RunLoop.main) → async;
//                          wait on $componentHealth, skip nil-lastUpdated seed.
//   3. ServiceManager    — identical shape to GatewayManager; use makeDcal().
//   4. DcalHealthMonitor — imperative applyFetchResult (widened to internal);
//                          drive nil and non-nil paths; assert both stamp.
//   5. DmailHealthMonitor — imperative applyFetchResult (widened to internal);
//                           drive true / false paths; assert both stamp.
//   6. EndpointHealthProbe — injectable probe seam; start(), await stamped
//                            emission, stop() to avoid 30-s task leak.
//
// No sleeps, no process spawning, no network I/O.
@MainActor
final class PublisherLastUpdatedTests: XCTestCase {

    // MARK: - 1. TailscaleMonitor

    func testTailscaleMonitor_constructionStamps_lastUpdated() {
        // The $status.map pipeline has no .receive(on:), so the initial @Published
        // replay fires synchronously during init; componentHealth is stamped before
        // the constructor returns.
        let monitor = TailscaleMonitor()
        XCTAssertNotNil(
            monitor.componentHealth.lastUpdated,
            "TailscaleMonitor componentHealth must be stamped at construction time"
        )
    }

    func testTailscaleMonitor_construction_mappedStatusIsDown() {
        // The seed status (.down) must map through tailscaleHealth to .down.
        let monitor = TailscaleMonitor()
        XCTAssertEqual(monitor.componentHealth.status, .down)
    }

    // MARK: - 2. GatewayManager

    func testGatewayManager_stampsLastUpdatedOnPublish() {
        // CombineLatest(isRunning, lastExitCode).receive(on: RunLoop.main) fires
        // once both seeded @Published values are available; this happens during
        // init WITHOUT calling start().  The timeout is a failure ceiling — NOT a sleep.
        let gw = GatewayManager()
        let exp = expectation(description: "GatewayManager stamps componentHealth.lastUpdated")
        var cancellable: AnyCancellable?
        cancellable = gw.$componentHealth
            .drop(while: { $0.lastUpdated == nil })
            .sink { health in
                XCTAssertNotNil(health.lastUpdated,
                    "GatewayManager componentHealth must carry a non-nil lastUpdated on first stamped publish")
                exp.fulfill()
            }
        wait(for: [exp], timeout: 2.0)
        cancellable?.cancel()
    }

    // MARK: - 3. ServiceManager (via makeDcal())

    func testServiceManager_dcal_stampsLastUpdatedOnPublish() {
        // ServiceManager uses the identical CombineLatest + .receive(on: RunLoop.main)
        // shape as GatewayManager — same test body, factory-built instance.
        let svc = ServiceManager.makeDcal()
        let exp = expectation(description: "ServiceManager(Dcal) stamps componentHealth.lastUpdated")
        var cancellable: AnyCancellable?
        cancellable = svc.$componentHealth
            .drop(while: { $0.lastUpdated == nil })
            .sink { health in
                XCTAssertNotNil(health.lastUpdated,
                    "ServiceManager componentHealth must carry a non-nil lastUpdated on first stamped publish")
                exp.fulfill()
            }
        wait(for: [exp], timeout: 2.0)
        cancellable?.cancel()
    }

    // MARK: - 4. DcalHealthMonitor

    func testDcalHealthMonitor_applyFetchResult_nil_stampsLastUpdated() {
        // Driving the nil (down) path through the widened applyFetchResult seam.
        let monitor = DcalHealthMonitor()
        monitor.applyFetchResult(nil)
        XCTAssertNotNil(monitor.componentHealth.lastUpdated,
            "DcalHealthMonitor must stamp lastUpdated even on a failed (nil) result")
        XCTAssertEqual(monitor.componentHealth.status, .down)
    }

    func testDcalHealthMonitor_applyFetchResult_ok_stampsLastUpdated() {
        // Driving the ok path — non-nil HealthResponse.
        let monitor = DcalHealthMonitor()
        let response = DcalHealthMonitor.HealthResponse(lastSync: "2026-06-24T12:00:00Z", eventCount: 42)
        monitor.applyFetchResult(response)
        XCTAssertNotNil(monitor.componentHealth.lastUpdated,
            "DcalHealthMonitor must stamp lastUpdated on a successful result")
        XCTAssertEqual(monitor.componentHealth.status, .ok)
    }

    // MARK: - 5. DmailHealthMonitor

    func testDmailHealthMonitor_applyFetchResult_false_stampsLastUpdated() {
        let monitor = DmailHealthMonitor()
        monitor.applyFetchResult(false)
        XCTAssertNotNil(monitor.componentHealth.lastUpdated,
            "DmailHealthMonitor must stamp lastUpdated on a failed (false) result")
        XCTAssertEqual(monitor.componentHealth.status, .down)
    }

    func testDmailHealthMonitor_applyFetchResult_true_stampsLastUpdated() {
        let monitor = DmailHealthMonitor()
        monitor.applyFetchResult(true)
        XCTAssertNotNil(monitor.componentHealth.lastUpdated,
            "DmailHealthMonitor must stamp lastUpdated on a successful (true) result")
        XCTAssertEqual(monitor.componentHealth.status, .ok)
    }

    // MARK: - 6. EndpointHealthProbe

    func testEndpointHealthProbe_start_stampsLastUpdated_okPath() async {
        // Fake probe — returns true immediately; no network I/O.
        let probe = EndpointHealthProbe(
            name: "Test",
            url: URL(string: "https://example.invalid")!,
            probe: { _ in true }
        )
        let exp = expectation(description: "EndpointHealthProbe stamps componentHealth.lastUpdated (ok)")
        var cancellable: AnyCancellable?
        cancellable = probe.$componentHealth
            .drop(while: { $0.lastUpdated == nil })
            .sink { health in
                XCTAssertNotNil(health.lastUpdated,
                    "EndpointHealthProbe must stamp lastUpdated when the probe returns true")
                XCTAssertEqual(health.status, .ok)
                exp.fulfill()
            }
        probe.start()
        await fulfillment(of: [exp], timeout: 2.0)
        probe.stop()
        cancellable?.cancel()
    }

    func testEndpointHealthProbe_start_stampsLastUpdated_downPath() async {
        // Fake probe — returns false immediately; no network I/O.
        let probe = EndpointHealthProbe(
            name: "Test",
            url: URL(string: "https://example.invalid")!,
            probe: { _ in false }
        )
        let exp = expectation(description: "EndpointHealthProbe stamps componentHealth.lastUpdated (down)")
        var cancellable: AnyCancellable?
        cancellable = probe.$componentHealth
            .drop(while: { $0.lastUpdated == nil })
            .sink { health in
                XCTAssertNotNil(health.lastUpdated,
                    "EndpointHealthProbe must stamp lastUpdated even when the probe returns false")
                XCTAssertEqual(health.status, .down)
                exp.fulfill()
            }
        probe.start()
        await fulfillment(of: [exp], timeout: 2.0)
        probe.stop()
        cancellable?.cancel()
    }
}
