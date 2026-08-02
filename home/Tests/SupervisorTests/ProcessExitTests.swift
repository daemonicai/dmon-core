import Foundation
import Testing
@testable import Supervisor

/// Carried over from block B3, where it was incidental coverage: 4.4 is what
/// makes both of these live failure modes rather than documented caveats —
/// the exit-before-source-installed race, and a stray second call for a pid
/// this process has already reaped.
@Suite
struct ProcessExitTests {
    /// The race `awaitExit`'s own documentation names: the process may have
    /// already exited (and become a zombie) before `DispatchSource
    /// .makeProcessSource` ever installs its watch. This must still resolve,
    /// not hang — bounded here by `withTimeout` so a regression back to
    /// "hangs" fails the assertion (`status` is `nil`) instead of hanging the
    /// whole test suite.
    ///
    /// The 200ms sleep is a best-effort way to *usually* win the race (give
    /// the child time to actually exit before `awaitExit` starts watching) —
    /// it is not a deterministic guarantee, since process scheduling is not
    /// under this test's control. Making it deterministic (e.g. blocking on
    /// some external signal that the kernel has already reaped the zombie
    /// state) is disproportionate to what this test is checking; the
    /// `withTimeout` bound is what makes an occasional miss harmless rather
    /// than a hang.
    @Test
    func awaitExitResolvesForAProcessThatAlreadyExitedBeforeBeingObserved() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "already-exited", executablePath: "/bin/sh", arguments: ["-c", "exit 3"])

        try await Task.sleep(nanoseconds: 200_000_000)

        // `withTimeout`'s "timed out" `nil` and `awaitExit`'s own
        // `.cancelled` are collapsed by `exitedStatus` into the same `nil`
        // here — this test only needs to tell "genuinely exited" apart from
        // either.
        let outcome = await withTimeout(2) { await awaitExit(of: child.pid) }
        let status = outcome?.exitedStatus
        #expect(status?.pid == child.pid)
        #expect(status?.exitCode == 3)
        #expect(status?.terminatingSignal == nil)
    }

    /// The other half of the single-owner contract: a pid this process has
    /// already reaped has no zombie left to observe, so a stray second call
    /// must not hang waiting for an event that can never come — bounded here
    /// so a regression back to "hangs" fails this assertion instead of the
    /// whole suite. But not hanging is only half the claim: the second call
    /// must also **not fabricate a successful exit**. An earlier version of
    /// `reap()` ignored `waitpid`'s failure (`ECHILD`, for a pid with no
    /// zombie left to collect) and read the untouched, zero-initialised
    /// `status` as "exited normally, code 0" — a fake success for a call
    /// that observed nothing at all. Asserting the outcome is `.reapFailed`,
    /// rather than discarding it, is what makes this test able to catch that
    /// regression; a version of this test that only checked "did not hang"
    /// would have passed against the fabricating `reap()` just as easily.
    @Test
    func aSecondAwaitExitForAnAlreadyReapedPidDoesNotHangAndDoesNotFabricateSuccess() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "double-reap", executablePath: "/bin/sh", arguments: ["-c", "exit 0"])

        let first = try #require(await awaitExit(of: child.pid).exitedStatus, "the first, genuine wait must observe a real exit")
        #expect(first.pid == child.pid)
        #expect(first.exitCode == 0)

        // The pid is now reaped; nothing genuinely spawned by this process
        // shares it (the window for the OS to reuse a pid this quickly is
        // not realistically reachable in a test).
        let second = await withTimeout(2) { await awaitExit(of: child.pid) }
        guard case .reapFailed = second else {
            Issue.record("expected .reapFailed for an already-reaped pid, got \(String(describing: second))")
            return
        }
    }

    /// The mechanism `HostSupervisor.shutdownChild`'s post-`SIGKILL` reap
    /// relies on: a plain, unstructured `Task` does not inherit or receive
    /// its *creating* task's cancellation. Racing the exact instant
    /// `shutdown()` would be cancelled mid-reap is not something this test
    /// can construct deterministically (the window between `SIGKILL` and
    /// the process actually dying is sub-millisecond) — so this proves the
    /// underlying property directly instead: cancel the task that *created*
    /// a wrapped `awaitExit` call, and confirm the wrapped call still
    /// observes the real exit rather than abandoning early.
    @Test
    func aTaskWrappedAwaitExitIsShieldedFromItsCreatingTasksCancellation() async throws {
        let spawner = ChildSpawner()
        let child = try await spawner.spawn(id: "shielded-reap", executablePath: "/bin/sh", arguments: ["-c", "sleep 30"])

        let outerTask = Task<AwaitExitOutcome, Never> {
            await Task { await awaitExit(of: child.pid) }.value
        }

        // Give the inner wait time to actually start watching before
        // cancelling the outer task.
        try await Task.sleep(nanoseconds: 50_000_000)
        outerTask.cancel()
        #expect(outerTask.isCancelled)

        #expect(spawner.killProcessGroup(of: child, signal: SIGKILL))
        let outcome = await withTimeout(2) { await outerTask.value }
        guard case .exited(let status) = outcome else {
            Issue.record("expected the shielded reap to observe a real exit despite the outer task's cancellation, got \(String(describing: outcome))")
            return
        }
        #expect(status.pid == child.pid)
    }
}
