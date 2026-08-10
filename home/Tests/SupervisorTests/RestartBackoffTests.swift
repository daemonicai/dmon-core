import Testing
@testable import Supervisor

@Suite
struct RestartBackoffTests {
    /// The crash-loop shape the brief named directly: a child that launches
    /// fine and dies immediately, repeatedly. Asserts the *actual sequence*
    /// and its cap, not merely that "some delay grew" — a test that only
    /// checked monotonic increase would also pass a sequence that grew once
    /// and then stalled.
    @Test
    func delaysDoubleOnSuccessiveUnstableExitsAndCapAtMaximum() {
        var backoff = RestartBackoff(initial: 2, maximum: 60, stabilityThreshold: 30)
        let delays = (0..<7).map { _ in backoff.nextRestartDelay(afterUptime: 0) }
        #expect(delays == [2, 4, 8, 16, 32, 60, 60])
    }

    /// Reset is keyed on the child having *stayed up*, not on a restart
    /// attempt merely having launched successfully (that would be dmonium's
    /// bug — see `RestartBackoff`'s own documentation). A child that just
    /// proved itself stable gets the initial delay on its very next restart,
    /// rather than continuing to pay the elevated delay a prior crash loop
    /// had reached.
    @Test
    func anExitAfterAStableRunGetsTheInitialDelayNotTheElevatedOne() {
        var backoff = RestartBackoff(initial: 2, maximum: 60, stabilityThreshold: 30)
        #expect(backoff.nextRestartDelay(afterUptime: 0) == 2)
        #expect(backoff.nextRestartDelay(afterUptime: 0) == 4)
        #expect(backoff.nextRestartDelay(afterUptime: 0) == 8)

        // The child then stays up past the threshold before this exit —
        // resetting the sequence rather than continuing to escalate from 16.
        #expect(backoff.nextRestartDelay(afterUptime: 45) == 2)

        // And the cycle escalates again from there if it keeps crashing.
        #expect(backoff.nextRestartDelay(afterUptime: 0) == 4)
    }

    @Test
    func hasReachedMaximumIsTrueOnlyAtTheCap() {
        var backoff = RestartBackoff(initial: 2, maximum: 8, stabilityThreshold: 30)
        let first = backoff.nextRestartDelay(afterUptime: 0)
        #expect(first == 2)
        #expect(!backoff.hasReachedMaximum(first))

        let second = backoff.nextRestartDelay(afterUptime: 0)
        #expect(second == 4)
        #expect(!backoff.hasReachedMaximum(second))

        let third = backoff.nextRestartDelay(afterUptime: 0)
        #expect(third == 8)
        #expect(backoff.hasReachedMaximum(third))
    }
}
