import Foundation
import Testing
@testable import Supervisor
#if canImport(Darwin)
import Darwin
#endif

/// `HostRuntime` — the app's composition root (tasks 4.6/4.7). Everything
/// here exercises the merged `[ChildStatus]` feed and the
/// shutdown-then-sweep sequence the app-target `AppDelegate` calls into, so
/// none of that logic is reachable only by running the app.
@Suite
struct HostRuntimeTests {
    /// "Health is visible" (spec requirement), plus the merge itself:
    /// `displayName` comes from the descriptor, `health` from
    /// `ChildHealthStore`, `supervision` from `ChildSupervisionStore` — a
    /// UI mirror needs to do none of that joining itself.
    @Test
    func startSpawnsTheEnabledChildAndPublishesMergedHealthAndSupervision() async throws {
        let descriptor = Self.spawnableDescriptor(id: "child-a", displayName: "Child A")
        let runtime = HostRuntime(
            children: [descriptor],
            monitors: [],
            healthChecker: HealthChecker(httpProbe: { _ in true }),
            healthCheckInterval: 0.02
        )

        await runtime.start()

        let sawHealthyChildA = await waitUntilTrue(timeout: 3) {
            await Self.currentStatuses(runtime).contains {
                $0.id == "child-a" && $0.displayName == "Child A" && $0.health == .healthy && $0.supervision == .normal
            }
        }
        #expect(sawHealthyChildA)

        _ = await runtime.shutdownForTermination()
    }

    /// Only enabled children are supervised (and only their ids appear in
    /// the merged feed) — a disabled descriptor and a read-only monitor are
    /// both fed to the health-check loop by `ChildInventory`-shaped
    /// callers, but neither is a "child" `HostSupervisor` tracks, so neither
    /// should ever show up in `statusUpdates()`.
    @Test
    func disabledChildrenAndMonitorsAreExcludedFromTheMergedFeed() async throws {
        let enabled = Self.spawnableDescriptor(id: "enabled-child", displayName: "Enabled")
        let disabled = ChildDescriptor(
            id: "disabled-child",
            displayName: "Disabled",
            transport: .loopbackHTTP,
            endpoint: URL(string: "http://127.0.0.1:9999/disabled")!,
            healthCheck: .http(URL(string: "http://127.0.0.1:9999/disabled")!),
            healthCheckTimeout: 1,
            startupOrder: 1,
            adoptionPolicy: .spawnOnly,
            launch: ChildLaunch(),
            isEnabled: false
        )
        let monitor = MonitorDescriptor(
            id: "a-monitor",
            displayName: "A Monitor",
            healthCheck: .http(URL(string: "http://127.0.0.1:9999/monitor")!),
            healthCheckTimeout: 1
        )
        let runtime = HostRuntime(
            children: [enabled, disabled],
            monitors: [monitor],
            healthChecker: HealthChecker(httpProbe: { _ in true }),
            healthCheckInterval: 0.02
        )

        await runtime.start()

        let sawEnabledOnly = await waitUntilTrue(timeout: 3) {
            let statuses = await Self.currentStatuses(runtime)
            let ids = Set(statuses.map(\.id))
            return ids.contains("enabled-child") && !ids.contains("disabled-child") && !ids.contains("a-monitor")
        }
        #expect(sawEnabledOnly)

        _ = await runtime.shutdownForTermination()
    }

    /// End-to-end through the exact call `AppDelegate` makes:
    /// `shutdownForTermination()` must actually terminate a real spawned
    /// process (not merely report success) and leave supervision showing
    /// `.stoppedIntentionally`, with no refusals. The child announces its
    /// own real pid via a file rather than relying on any internal
    /// `HostSupervisor` accessor, since `HostRuntime` does not expose one.
    @Test
    func shutdownForTerminationKillsTheSpawnedChildAndReportsNoRefusals() async throws {
        let pidFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: pidFile) }
        let descriptor = Self.spawnableDescriptor(
            id: "child-a",
            displayName: "Child A",
            command: "echo $$ > \"\(pidFile.path)\"\nsleep 30"
        )
        let runtime = HostRuntime(
            children: [descriptor],
            monitors: [],
            healthChecker: HealthChecker(httpProbe: { _ in true }),
            healthCheckInterval: 0.02
        )

        await runtime.start()
        let pid = try await Self.waitForPid(at: pidFile)
        #expect(kill(pid, 0) == 0, "child should be alive before shutdown")

        let refused = await runtime.shutdownForTermination()

        #expect(refused.isEmpty)
        #expect(kill(pid, 0) != 0, "shutdownForTermination should have killed the spawned child")

        let statuses = await Self.currentStatuses(runtime)
        #expect(statuses.first { $0.id == "child-a" }?.supervision == .stoppedIntentionally)
    }

    /// Requirement 5 / design D6's exact defect, closed by this
    /// remediation: a leader that exits promptly on `SIGTERM` must not let
    /// a still-alive group member escape termination. The leader here has
    /// no trap of its own — `ChildSpawner.spawn` resets every signal to its
    /// default disposition, so it dies the instant the group is signalled,
    /// well inside `gracefulShutdownTimeout` — while a backgrounded
    /// grandchild explicitly ignores `SIGTERM` (`trap '' TERM`, which
    /// survives `exec` into `sleep`) and would run its full 30s sleep
    /// undisturbed if anything here decided whether to escalate by the
    /// leader's liveness alone. `handleExit` also clears this child from
    /// `HostSupervisor`'s own state as soon as the leader is reaped —
    /// before this assertion ever runs — so the grandchild's death here
    /// cannot come from any path keyed off that state; only the group kill
    /// on `shutdownChild`'s ordinary graceful-exit branch reaches it.
    @Test
    func shutdownForTerminationKillsAGrandchildEvenWhenTheLeaderExitsPromptlyOnItsOwn() async throws {
        let pidFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: pidFile) }
        let descriptor = Self.spawnableDescriptor(
            id: "child-a",
            displayName: "Child A",
            command: "(trap '' TERM; sleep 30) &\necho $! > \"\(pidFile.path)\"\nwait"
        )
        let runtime = HostRuntime(
            children: [descriptor],
            monitors: [],
            healthChecker: HealthChecker(httpProbe: { _ in true }),
            healthCheckInterval: 0.02
        )

        await runtime.start()
        let grandchildPid = try await Self.waitForPid(at: pidFile)
        #expect(kill(grandchildPid, 0) == 0, "grandchild should be alive before shutdown")

        let refused = await runtime.shutdownForTermination()

        #expect(refused.isEmpty)
        // Polled, not a bare synchronous check: the grandchild is re-parented
        // to launchd once its leader is reaped, so this process is not its
        // parent and has no `wait()`-based synchronization point for when the
        // delivered `SIGKILL` actually finishes taking it down — only that
        // `kill(-pgid, SIGKILL)` was already sent by the time
        // `shutdownForTermination()` returned. Bounded well below the test's
        // own patience, not `gracefulShutdownTimeout`: a real defect here
        // would leave the grandchild alive indefinitely, not merely slow to
        // die, so this timeout is about tolerating scheduling latency, not
        // masking the very defect this test exists to catch.
        let grandchildDied = await waitUntilTrue(timeout: 2) { kill(grandchildPid, 0) != 0 }
        #expect(grandchildDied, "shutdownForTermination should have killed the grandchild even though the leader exited on its own")
    }

    /// R5: the one fact an app-exit termination budget must derive from,
    /// exposed through `HostRuntime` rather than restated. `nonisolated`,
    /// so no `await` is needed to read it.
    @Test
    func worstCaseShutdownDurationDerivesFromTheSupervisorsOwnValue() {
        let descriptor = Self.spawnableDescriptor(id: "child-a", displayName: "Child A")
        let runtime = HostRuntime(
            children: [descriptor],
            monitors: [],
            gracefulShutdownTimeout: 9
        )

        #expect(runtime.worstCaseShutdownDuration == 9)
    }

    /// The positive case: `ChildInventory.networkGateway` is enabled, and a
    /// `HostRuntime` constructed with it (directly, not via the `children`
    /// default) reports the gateway as enabled.
    @Test
    func isGatewayEnabledIsTrueWhenTheNetworkGatewayDescriptorIsEnabled() {
        let runtime = HostRuntime(children: [ChildInventory.networkGateway], monitors: [])

        #expect(runtime.isGatewayEnabled)
    }

    /// The negative case this fact exists to answer correctly: a
    /// `HostRuntime` built from a *disabled* gateway descriptor — same id,
    /// `isEnabled: false` — must not report the gateway as enabled just
    /// because a descriptor with that id exists in the set.
    @Test
    func isGatewayEnabledIsFalseWhenTheNetworkGatewayDescriptorIsDisabled() {
        let disabledGateway = ChildDescriptor(
            id: ChildInventory.networkGateway.id,
            displayName: ChildInventory.networkGateway.displayName,
            transport: ChildInventory.networkGateway.transport,
            endpoint: ChildInventory.networkGateway.endpoint,
            healthCheck: ChildInventory.networkGateway.healthCheck,
            healthCheckTimeout: ChildInventory.networkGateway.healthCheckTimeout,
            startupOrder: ChildInventory.networkGateway.startupOrder,
            adoptionPolicy: ChildInventory.networkGateway.adoptionPolicy,
            launch: ChildInventory.networkGateway.launch,
            isEnabled: false
        )
        let runtime = HostRuntime(children: [disabledGateway], monitors: [])

        #expect(!runtime.isGatewayEnabled)
    }

    /// Also false when the gateway descriptor is absent from the set
    /// entirely — `isGatewayEnabled` derives from what this runtime was
    /// actually constructed with, not a standing assumption that the
    /// gateway is always present.
    @Test
    func isGatewayEnabledIsFalseWhenNoGatewayDescriptorIsPresent() {
        let descriptor = Self.spawnableDescriptor(id: "child-a", displayName: "Child A")
        let runtime = HostRuntime(children: [descriptor], monitors: [])

        #expect(!runtime.isGatewayEnabled)
    }

    // MARK: - Helpers

    private static func spawnableDescriptor(
        id: ChildID,
        displayName: String,
        command: String = "sleep 30"
    ) -> ChildDescriptor {
        let endpoint = URL(string: "http://127.0.0.1:9999/\(id.rawValue)")!
        return ChildDescriptor(
            id: id,
            displayName: displayName,
            transport: .loopbackHTTP,
            endpoint: endpoint,
            healthCheck: .http(endpoint),
            healthCheckTimeout: 1,
            startupOrder: 0,
            adoptionPolicy: .spawnOnly,
            launch: ChildLaunch(candidates: [.absolutePath("/bin/sh")], arguments: ["-c", command]),
            isEnabled: true
        )
    }

    private static func currentStatuses(_ runtime: HostRuntime) async -> [ChildStatus] {
        var iterator = await runtime.statusUpdates().makeAsyncIterator()
        return await iterator.next() ?? []
    }

    private static func waitForPid(at url: URL, timeout: TimeInterval = 2) async throws -> pid_t {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let contents = try? String(contentsOf: url, encoding: .utf8),
               let pid = pid_t(contents.trimmingCharacters(in: .whitespacesAndNewlines)) {
                return pid
            }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        struct TimedOutWaitingForPidFile: Error {}
        throw TimedOutWaitingForPidFile()
    }
}
