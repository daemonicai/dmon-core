import Testing
@testable import Supervisor

@Suite
struct ChildInventoryTests {
    @Test
    func inventoryHasSixSupervisedChildrenAndFourMonitors() {
        #expect(ChildInventory.children.count == 6)
        #expect(ChildInventory.monitors.count == 4)
    }

    @Test
    func onlyTheNetworkGatewayIsEnabled() {
        let enabled = ChildInventory.children.filter(\.isEnabled)
        #expect(enabled.map(\.id) == [ChildInventory.networkGateway.id])
    }

    @Test
    func everySupervisedChildDeclaresAllSixDimensions() {
        for child in ChildInventory.children {
            #expect(child.healthCheck != .none)
            #expect(child.healthCheckTimeout > 0)
        }
    }

    /// Every enabled child must actually be launchable; children not yet started
    /// may legitimately carry no launch candidates (mlx's uv venv, the unbuilt
    /// sidecar). Iterates rather than naming the gateway, so a later block that
    /// flips another child's `isEnabled` without adding candidates fails here.
    @Test
    func theEnabledChildDeclaresLaunchCandidates() {
        let enabled = ChildInventory.children.filter(\.isEnabled)
        #expect(!enabled.isEmpty)
        for child in enabled {
            #expect(!child.launch.candidates.isEmpty)
        }
    }

    @Test
    func everyMonitorDeclaresAHealthCheck() {
        for monitor in ChildInventory.monitors {
            #expect(monitor.healthCheck != .none)
            #expect(monitor.healthCheckTimeout > 0)
        }
    }

    @Test
    func supervisedChildIdsAreUnique() {
        let ids = Set(ChildInventory.children.map(\.id))
        #expect(ids.count == ChildInventory.children.count)
    }

    @Test
    func monitorIdsAreUnique() {
        let ids = Set(ChildInventory.monitors.map(\.id))
        #expect(ids.count == ChildInventory.monitors.count)
    }

    @Test
    func noIdIsSharedBetweenChildrenAndMonitors() {
        let childIds = Set(ChildInventory.children.map(\.id))
        let monitorIds = Set(ChildInventory.monitors.map(\.id))
        #expect(childIds.isDisjoint(with: monitorIds))
    }

    @Test
    func startupOrdersAreDistinct() {
        let orders = Set(ChildInventory.children.map(\.startupOrder))
        #expect(orders.count == ChildInventory.children.count)
    }
}
