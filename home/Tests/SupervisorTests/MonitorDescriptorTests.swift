import Foundation
import Testing
@testable import Supervisor

@Suite
struct MonitorDescriptorTests {
    @Test
    func carriesIdentityDisplayNameAndHealthCheck() {
        let monitor = MonitorDescriptor(
            id: "sample-monitor",
            displayName: "Sample Monitor",
            healthCheck: .http(URL(string: "http://127.0.0.1:9999")!),
            healthCheckTimeout: 5
        )
        #expect(monitor.id == ChildID("sample-monitor"))
        #expect(monitor.displayName == "Sample Monitor")
        #expect(monitor.healthCheck == .http(URL(string: "http://127.0.0.1:9999")!))
    }

    @Test
    func equalMonitorsCompareEqual() {
        let a = MonitorDescriptor(id: "m", displayName: "M", healthCheck: .none, healthCheckTimeout: 1)
        let b = MonitorDescriptor(id: "m", displayName: "M", healthCheck: .none, healthCheckTimeout: 1)
        #expect(a == b)
    }
}
