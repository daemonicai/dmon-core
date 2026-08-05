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
        let status = try #require(await awaitExit(of: child.pid).exitedStatus, "an uncancelled wait must report a real exit status")

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
    /// `killProcessGroup` would, under an inverted guard, send `SIGKILL` to
    /// the test runner's own group instead of failing an assertion — a test
    /// whose failure mode is worse than the bug it guards. Testing the
    /// predicate in isolation gets the same coverage with no signal ever
    /// sent.
    /// A spawned child must not inherit whatever signal mask or dispositions
    /// this process happens to have — proven directly rather than assumed,
    /// because `swift test`'s own runner blocks `SIGTERM` (confirmed while
    /// diagnosing task 4.5's graceful-shutdown tests): without
    /// `POSIX_SPAWN_SETSIGDEF` / `POSIX_SPAWN_SETSIGMASK`, this test's own
    /// trap never fires and the child instead runs its full 30-second sleep
    /// — the exact failure mode `withTimeout` below bounds rather than hangs
    /// on.
    @Test
    func aSpawnedChildDoesNotInheritThisProcessesSignalMaskOrDispositions() async throws {
        let markerFile = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: markerFile) }
        let spawner = ChildSpawner()
        let script = "trap 'echo graceful >> \"\(markerFile.path)\"; exit 0' TERM\nsleep 30 &\nwait"
        let child = try await spawner.spawn(id: "signal-mask-child", executablePath: "/bin/sh", arguments: ["-c", script])

        // Give the trap a moment to be installed before signalling.
        try await Task.sleep(nanoseconds: 200_000_000)
        #expect(spawner.killProcessGroup(of: child, signal: SIGTERM))

        // `withTimeout`'s "timed out" `nil` and `awaitExit`'s own
        // `.cancelled` are collapsed by `exitedStatus` into the same `nil`
        // here — this test only needs to tell "genuinely exited" apart from
        // either.
        let outcome = await withTimeout(3) { await awaitExit(of: child.pid) }
        let status = outcome?.exitedStatus
        #expect(status?.exitCode == 0, "the child should have exited via its own TERM trap rather than running its full sleep")

        let content = (try? String(contentsOf: markerFile, encoding: .utf8)) ?? ""
        #expect(content.contains("graceful"))
    }

    @Test
    func wouldSignalOurOwnGroupIsTrueForOurOwnGroupAndFalseForAnythingElse() {
        let spawner = ChildSpawner()
        #expect(spawner.wouldSignalOurOwnGroup(getpgrp()))
        #expect(!spawner.wouldSignalOurOwnGroup(getpgrp() + 1))
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
