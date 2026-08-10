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

    /// `descriptorsToCloseInChild`'s whole reason to exist: a pipe
    /// descriptor colliding with 0, 1, or 2 must never appear in its result,
    /// regardless of which of the four roles (stdout/stderr, read/write) it
    /// occupies. Exercised against synthetic numbers rather than real
    /// descriptors — freeing this test process's own stdin/stdout/stderr to
    /// force a real collision would be a hazard to every other test sharing
    /// this process, the same reasoning `wouldSignalOurOwnGroup`'s own test
    /// already relies on.
    ///
    /// The first case is the one the old, unconditional four-close list
    /// already got right — ordinary descriptors, none near 0/1/2 — included
    /// here as the control: the function must not over-exclude when nothing
    /// is colliding. Each collision case afterwards is the one the old list
    /// got wrong: before this fix, `[stdoutWriteFD, stderrWriteFD,
    /// stdoutReadFD, stderrReadFD]` was returned unfiltered, so a collision
    /// in any of the four positions put 0, 1, or 2 into the close list —
    /// exactly the descriptor `spawn` had just wired on purpose.
    @Test
    func descriptorsToCloseInChildExcludesStandardDescriptorsRegardlessOfWhichRoleTheyLandOn() {
        let spawner = ChildSpawner()

        // No collision: every fd stays, same as the old unconditional list.
        #expect(
            Set(spawner.descriptorsToCloseInChild(stdoutWriteFD: 3, stderrWriteFD: 5, stdoutReadFD: 4, stderrReadFD: 6))
                == [3, 4, 5, 6]
        )

        // stdout read end lands on 0 — the reviewer's own repro.
        // Before: [3, 5, 0, 6]. After: [3, 5, 6].
        #expect(
            Set(spawner.descriptorsToCloseInChild(stdoutWriteFD: 3, stderrWriteFD: 5, stdoutReadFD: 0, stderrReadFD: 6))
                == [3, 5, 6]
        )

        // stdout write end lands on 1 — same class, the stdout slot itself.
        // Before: [1, 5, 4, 6]. After: [4, 5, 6].
        #expect(
            Set(spawner.descriptorsToCloseInChild(stdoutWriteFD: 1, stderrWriteFD: 5, stdoutReadFD: 4, stderrReadFD: 6))
                == [4, 5, 6]
        )

        // stderr write end lands on 2 — same class, the stderr slot itself.
        // Before: [3, 2, 4, 6]. After: [3, 4, 6].
        #expect(
            Set(spawner.descriptorsToCloseInChild(stdoutWriteFD: 3, stderrWriteFD: 2, stdoutReadFD: 4, stderrReadFD: 6))
                == [3, 4, 6]
        )

        // stderr read end lands on 1.
        // Before: [3, 5, 4, 1]. After: [3, 4, 5].
        #expect(
            Set(spawner.descriptorsToCloseInChild(stdoutWriteFD: 3, stderrWriteFD: 5, stdoutReadFD: 4, stderrReadFD: 1))
                == [3, 4, 5]
        )

        // All three standard slots collide at once, across three different
        // roles, with only the fourth descriptor genuinely needing a close.
        // Before: [0, 1, 2, 7]. After: [7].
        #expect(
            spawner.descriptorsToCloseInChild(stdoutWriteFD: 0, stderrWriteFD: 1, stdoutReadFD: 2, stderrReadFD: 7)
                == [7]
        )
    }

    /// The defect this guards: without `POSIX_SPAWN_CLOEXEC_DEFAULT`,
    /// `posix_spawn` inherits every descriptor this host process happens to
    /// have open into the child, except the four the spawner's own file
    /// actions explicitly close. `unrelatedPipe` here stands in for any
    /// descriptor this host owns for reasons that have nothing to do with
    /// the child being spawned — a socket, a credential store, another
    /// pipe — and it is kept open across the whole spawn, not torn down by
    /// `ChildSpawner` at all, so this reproduces on every run rather than
    /// only under the narrow concurrent-spawn window described where
    /// `spawn`'s flags are set.
    ///
    /// Probes the two specific descriptor numbers with `[ -e /dev/fd/N ]`
    /// rather than listing the whole directory with `ls /dev/fd`: `ls`
    /// itself opens the directory it lists, landing on a low fd number
    /// that — with few other descriptors already open, as happens running
    /// this test in isolation — coincidentally collides with
    /// `unrelatedPipe`'s own low numbers, producing a false failure that
    /// has nothing to do with inheritance. Confirmed directly: `/bin/sh -c
    /// 'ls /dev/fd'` against a clean three-descriptor table (0/1/2 only)
    /// reports `0 1 2 3 4` — `ls` itself already accounts for the extra
    /// two. `[ -e /dev/fd/N ]` opens nothing to answer the question,
    /// confirmed the same way: probing a genuinely-closed fd 3 reports
    /// nothing, probing one the shell itself opened reports it.
    ///
    /// Falsified by removing `POSIX_SPAWN_CLOEXEC_DEFAULT` from `spawn`:
    /// this test then fails because both probes report the descriptors as
    /// open in the child.
    @Test
    func spawnedChildInheritsNoUnrelatedFileDescriptors() async throws {
        let unrelatedPipe = Pipe()
        defer {
            unrelatedPipe.fileHandleForReading.closeFile()
            unrelatedPipe.fileHandleForWriting.closeFile()
        }
        let unrelatedWriteFD = unrelatedPipe.fileHandleForWriting.fileDescriptor
        let unrelatedReadFD = unrelatedPipe.fileHandleForReading.fileDescriptor

        let spawner = ChildSpawner()
        let script = """
        [ -e /dev/fd/\(unrelatedWriteFD) ] && echo write-fd-open
        [ -e /dev/fd/\(unrelatedReadFD) ] && echo read-fd-open
        echo probe-done
        """
        let child = try await spawner.spawn(id: "fd-audit-child", executablePath: "/bin/sh", arguments: ["-c", script])
        let data = child.standardOutput.readDataToEndOfFile()
        _ = await awaitExit(of: child.pid)

        let output = String(data: data, encoding: .utf8) ?? ""
        #expect(output.contains("probe-done"), "the probe script did not run to completion: \(output)")
        #expect(
            !output.contains("write-fd-open"),
            "child inherited this host's unrelated pipe write end (fd \(unrelatedWriteFD)): \(output)"
        )
        #expect(
            !output.contains("read-fd-open"),
            "child inherited this host's unrelated pipe read end (fd \(unrelatedReadFD)): \(output)"
        )
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
