import Foundation
import Testing
@testable import Supervisor

/// Proves the requirement's teeth: adding a child to the inventory is adding a
/// value, never a type. Both a hypothetical supervised child and a hypothetical
/// monitor are built here from nothing but `Supervisor`'s existing public types.
@Suite
struct ChildInventoryExtensibilityTests {
    @Test
    func aHypotheticalSeventhChildNeedsNoModelChange() {
        let logShipper = ChildDescriptor(
            id: "log-shipper",
            displayName: "Log Shipper",
            transport: .socket,
            endpoint: URL(string: "http://127.0.0.1:9100")!,
            healthCheck: .tcp(host: "127.0.0.1", port: 9100),
            healthCheckTimeout: 3,
            startupOrder: 6,
            adoptionPolicy: .spawnOnly,
            launch: ChildLaunch(candidates: [.absolutePath("/usr/local/bin/log-shipper")], arguments: ["--config", "log.yaml"]),
            isEnabled: false
        )

        #expect(logShipper.id == ChildID("log-shipper"))
        #expect(logShipper.adoptionPolicy == .spawnOnly)

        let extendedInventory = ChildInventory.children + [logShipper]
        #expect(extendedInventory.count == ChildInventory.children.count + 1)
    }

    @Test
    func aHypotheticalFifthMonitorNeedsNoModelChange() {
        let diskSpace = MonitorDescriptor(
            id: "disk-space",
            displayName: "Disk Space",
            healthCheck: .process(executablePath: "df", arguments: ["-h"]),
            healthCheckTimeout: 2
        )

        let extendedMonitors = ChildInventory.monitors + [diskSpace]
        #expect(extendedMonitors.count == ChildInventory.monitors.count + 1)
    }
}
