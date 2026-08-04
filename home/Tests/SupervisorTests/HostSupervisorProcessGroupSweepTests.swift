import Foundation
import Testing
@testable import Supervisor
#if canImport(Darwin)
import Darwin
#endif

/// `HostSupervisor.terminateSpawnedProcessGroups()` — the exit-sweep backstop
/// (task 4.6). Exercised independently of `shutdown()` throughout, since the
/// whole point of the sweep is that it works even when `shutdown()` did not
/// run to completion.
@Suite
struct HostSupervisorProcessGroupSweepTests {
    /// The sweep's core claim: it can kill a still-spawned child entirely on
    /// its own, without `shutdown()` ever having run. This is what makes it
    /// a genuine backstop rather than dead code only reachable after
    /// `shutdown()` has already cleared everything it would touch.
    @Test
    func sweepTerminatesAChildLeftSpawnedIndependentlyOfShutdown() async throws {
        let descriptor = Self.descriptor(id: "leftover-child", command: "sleep 30")
        let supervisor = HostSupervisor(descriptors: [descriptor], store: ChildSupervisionStore())

        await supervisor.start()
        let pid = try #require(await supervisor.currentPID(for: descriptor.id))
        #expect(kill(pid, 0) == 0, "child should be alive before the sweep")

        let refused = await supervisor.terminateSpawnedProcessGroups()

        let died = await waitUntilTrue(timeout: 2) { kill(pid, 0) != 0 }
        #expect(died, "the sweep should have killed the still-spawned child")
        #expect(refused.isEmpty)
    }

    /// After a clean `shutdown()`, `handleExit` has already cleared
    /// `spawnedChild` for the one child in this test — the sweep must find
    /// nothing left to do, proving it is genuinely a no-op backstop rather
    /// than something that re-signals an already-terminated child.
    @Test
    func sweepFindsNothingAfterACleanShutdown() async throws {
        let descriptor = Self.descriptor(id: "graceful-child", command: "trap 'exit 0' TERM; sleep 30 & wait")
        let supervisor = HostSupervisor(descriptors: [descriptor], store: ChildSupervisionStore(), gracefulShutdownTimeout: 3)

        await supervisor.start()
        _ = try #require(await supervisor.currentPID(for: descriptor.id))
        await supervisor.shutdown()

        #expect(await supervisor.currentPID(for: descriptor.id) == nil, "a cleanly shut-down child should have no live pid left")

        let refused = await supervisor.terminateSpawnedProcessGroups()
        #expect(refused.isEmpty)
    }

    /// "Adoption is exempt", proven with a real backing process rather than
    /// by inspecting the optional: a genuine process, spawned entirely
    /// outside this `HostSupervisor`'s own bookkeeping (standing in for
    /// something that was already running before the host started, which is
    /// exactly what adoption means), must still be alive after the sweep —
    /// alongside proof that a genuinely spawned sibling child in the same
    /// supervisor *is* killed, so this is not merely "the sweep does
    /// nothing".
    @Test
    func sweepNeverTouchesAnAdoptedChildsBackingProcess() async throws {
        let standinSpawner = ChildSpawner()
        let standin = try await standinSpawner.spawn(id: "standin", executablePath: "/bin/sh", arguments: ["-c", "sleep 30"])
        defer {
            _ = standinSpawner.killProcessGroup(of: standin)
        }

        let coordinator = ChildStartCoordinator(healthChecker: HealthChecker(httpProbe: { _ in true }))
        let adoptedDescriptor = Self.descriptor(id: "adopted-child", policy: .adoptOrSpawn, command: "sleep 30")
        let spawnedDescriptor = Self.descriptor(id: "genuinely-spawned", command: "sleep 30")
        let supervisor = HostSupervisor(
            descriptors: [adoptedDescriptor, spawnedDescriptor],
            coordinator: coordinator,
            store: ChildSupervisionStore()
        )

        await supervisor.start()
        #expect(
            await supervisor.currentPID(for: adoptedDescriptor.id) == nil,
            "an adoptOrSpawn descriptor whose endpoint already answers should adopt, not spawn"
        )
        let spawnedPid = try #require(await supervisor.currentPID(for: spawnedDescriptor.id))

        let refused = await supervisor.terminateSpawnedProcessGroups()

        let spawnedDied = await waitUntilTrue(timeout: 2) { kill(spawnedPid, 0) != 0 }
        #expect(spawnedDied, "the genuinely spawned sibling should be killed by the sweep")
        #expect(refused.isEmpty)
        #expect(kill(standin.pid, 0) == 0, "the process standing in for the adopted child must survive the sweep")

        _ = await awaitExit(of: spawnedPid)
    }

    // MARK: - Helpers

    private static func descriptor(
        id: ChildID,
        policy: AdoptionPolicy = .spawnOnly,
        command: String
    ) -> ChildDescriptor {
        let endpoint = URL(string: "http://127.0.0.1:9999/unused")!
        return ChildDescriptor(
            id: id,
            displayName: id.rawValue,
            transport: .loopbackHTTP,
            endpoint: endpoint,
            healthCheck: .http(endpoint),
            healthCheckTimeout: 1,
            startupOrder: 0,
            adoptionPolicy: policy,
            launch: ChildLaunch(candidates: [.absolutePath("/bin/sh")], arguments: ["-c", command]),
            isEnabled: true
        )
    }
}
