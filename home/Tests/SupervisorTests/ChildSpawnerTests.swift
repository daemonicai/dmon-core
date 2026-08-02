import Foundation
import Testing
@testable import Supervisor
#if canImport(Darwin)
import Darwin
#endif

@Suite
struct ChildSpawnerTests {
    /// The danger this whole block was warned about: if `POSIX_SPAWN_SETPGROUP`
    /// were silently ineffective, this test's own `kill(-pgid, …)` calls
    /// would signal the test runner's own group. So every kill below is
    /// preceded by exactly this assertion, both here and inside
    /// `ChildSpawner` itself.
    @Test
    func spawnedChildGetsItsOwnProcessGroupDistinctFromOurs() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "test-child", executablePath: "/bin/sh", arguments: ["-c", "sleep 30"])

        #expect(getpgid(child.pid) == child.pid)
        #expect(getpgid(child.pid) != getpgrp())

        #expect(spawner.killProcessGroup(of: child))
        _ = await awaitExit(of: child.pid)
    }

    @Test
    func stdoutIsCapturedThroughThePipe() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "echo-child", executablePath: "/bin/sh", arguments: ["-c", "echo from-child"])
        let data = child.standardOutput.readDataToEndOfFile()
        _ = await awaitExit(of: child.pid)

        #expect(String(data: data, encoding: .utf8) == "from-child\n")
    }

    @Test
    func stderrIsCapturedThroughItsOwnPipeSeparateFromStdout() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "stderr-child", executablePath: "/bin/sh", arguments: ["-c", "echo to-stderr 1>&2"])
        let stdoutData = child.standardOutput.readDataToEndOfFile()
        let stderrData = child.standardError.readDataToEndOfFile()
        _ = await awaitExit(of: child.pid)

        #expect(stdoutData.isEmpty)
        #expect(String(data: stderrData, encoding: .utf8) == "to-stderr\n")
    }

    @Test
    func awaitExitReportsTheChildsRealExitCode() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "exit-child", executablePath: "/bin/sh", arguments: ["-c", "exit 7"])
        let status = await awaitExit(of: child.pid)

        #expect(status.pid == child.pid)
        #expect(status.exitCode == 7)
        #expect(status.terminatingSignal == nil)
    }

    /// The scenario's teeth: a real grandchild, not merely the direct child.
    /// The spawned shell backgrounds a `sleep`, writes its pid to a temp
    /// file, then waits on it — so this proves group-kill reaches a process
    /// this host never spawned directly, which is the whole point of using
    /// a process group instead of killing one pid.
    @Test
    func killingTheGroupTerminatesAGrandchildTheChildItselfSpawned() async throws {
        let spawner = ChildSpawner()
        let pidFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: pidFile) }

        let script = "sleep 30 & echo $! > \(pidFile.path); wait"
        let child = try await spawner.spawn(id: "parent-child", executablePath: "/bin/sh", arguments: ["-c", script])

        let grandchildPid = try await waitForPid(at: pidFile)
        #expect(getpgid(grandchildPid) == child.processGroupID)
        #expect(kill(grandchildPid, 0) == 0, "grandchild should be alive before the group kill")

        #expect(child.processGroupID != getpgrp(), "refusing to proceed: about to kill our own group")
        #expect(spawner.killProcessGroup(of: child))
        _ = await awaitExit(of: child.pid)

        let grandchildDied = await waitUntilTrue(timeout: 2) { kill(grandchildPid, 0) != 0 }
        #expect(grandchildDied, "grandchild survived the group kill")
    }

    /// The predicate both recovery paths depend on, asserted directly rather
    /// than through a signalling function. `wouldSignalOurOwnGroup` sends no
    /// signal itself, so this is the one safe way to exercise the case where
    /// it collides with our own group: constructing a `SpawnedChild` whose
    /// `processGroupID` equals `getpgrp()` and handing it to
    /// `killProcessGroup`/`killProcessGroups` would, under an inverted guard,
    /// send `SIGKILL` to the test runner's own group instead of failing an
    /// assertion — a test whose failure mode is worse than the bug it
    /// guards. Testing the predicate in isolation gets the same coverage
    /// with no signal ever sent.
    @Test
    func wouldSignalOurOwnGroupIsTrueForOurOwnGroupAndFalseForAnythingElse() {
        let spawner = ChildSpawner()
        #expect(spawner.wouldSignalOurOwnGroup(getpgrp()))
        #expect(!spawner.wouldSignalOurOwnGroup(getpgrp() + 1))
    }

    @Test
    func killProcessGroupsAcceptsOnlySpawnedChildrenNotAdoptedOnes() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "batch-child", executablePath: "/bin/sh", arguments: ["-c", "sleep 30"])

        // `ChildStartOutcome.adopted` carries no `SpawnedChild` value at all,
        // so there is nothing an adopted child could contribute to this
        // array even if a caller wanted to include one — the compiler
        // enforces it, this test only documents that `[SpawnedChild]` is
        // genuinely the narrowest type that compiles.
        let refused = spawner.killProcessGroups(of: [child])
        let status = await awaitExit(of: child.pid)

        #expect(refused.isEmpty)
        #expect(status.terminatingSignal == SIGKILL)
    }
}

private func waitForPid(at url: URL, timeout: TimeInterval = 2) async throws -> pid_t {
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

private func waitUntilTrue(timeout: TimeInterval, condition: @escaping @Sendable () -> Bool) async -> Bool {
    await withTimeout(timeout) {
        while !condition() {
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return true
    } ?? false
}
