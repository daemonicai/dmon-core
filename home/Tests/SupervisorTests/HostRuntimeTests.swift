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
