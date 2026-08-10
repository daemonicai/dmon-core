import Foundation
import Testing
@testable import GatewayClient

/// Consumes snapshots from `stream` until one satisfies `predicate`, returning it — or fails the
/// test loudly (via `Issue.record`) and returns `nil` if `timeout` elapses first.
///
/// # Why this exists
///
/// A test that does "trigger some action, then assert on the very next snapshot the update stream
/// yields" is making an ordering assumption: that nothing else can land in that exact slot between
/// the trigger and the observation. That assumption holds on a quiet, fast machine, but breaks
/// under load — a concurrently in-flight write completing, an intermediate reducer step, or plain
/// scheduler contention on a busy CI runner can all cause an unrelated snapshot to be yielded
/// first. The test then inspects the *wrong* snapshot and fails for a reason that has nothing to
/// do with the behaviour actually under test — the exact pattern behind
/// `SessionCoordinatorTests`' ~50% CI flake rate (see the CI-stabilisation DEVLOG entries this
/// helper was added for). This drains the stream until the condition being tested is actually
/// true, so the assertion is about the condition, not about which slot it happened to land in.
///
/// This is a **hang guard, not a discriminator**: `timeout` exists only so a genuinely broken
/// implementation — one that never publishes a snapshot satisfying `predicate`, and never ends the
/// stream either — fails this test instead of hanging it forever. Keep it generous. A correct
/// implementation that is merely slow (a loaded runner, a real network round trip) must still
/// pass; a broken implementation never satisfies `predicate` at any budget, so the exact number
/// chosen carries no meaning beyond "long enough that a genuine hang, not a slow pass, is what
/// trips it."
///
/// # Calling convention: pass the stream, not an iterator
///
/// Callers keep the `AsyncStream<SessionSnapshot>` returned by `coordinator.updates()` around
/// (not only an `Iterator` made from it) and pass that stream here. This is required, not
/// stylistic: see the next section for why an `Iterator` cannot cross the task boundary this
/// helper needs, and the "one subscriber" note below for why calling `makeAsyncIterator()`
/// multiple times on the same stream is safe. A test can still keep its own `var iterator =
/// stream.makeAsyncIterator()` for its own direct `await iterator.next()` calls elsewhere —
/// `AsyncStream` multiplexes every `Iterator` made from the same stream value onto one shared
/// buffer (verified empirically: two iterators from the same stream hand out elements 1, 2, 3 in
/// order between them, never replaying), so this helper's internal reads and a test's own direct
/// reads never duplicate or skip a snapshot between them.
///
/// # Why the timeout needs a real race, not `.timeLimit` or a polling loop
///
/// `AsyncStream.Iterator.next()` suspends on a plain, non-cancellation-aware continuation. Swift
/// Testing's `.timeLimit` trait only cancels the *test's* task cooperatively — it cannot force a
/// suspended `next()` to return — and a `withTaskGroup`-based race waits for every child task it
/// started, including a permanently stuck one, before returning at all. Both are documented,
/// independently reproduced dead ends: see
/// `tech-debt/swift-testing-timelimit-does-not-bound-continuation-hangs.md` and
/// `HostSupervisorChildOutputTests.cancellingAReaderTaskEndsItEvenWhenEOFWillNeverArrive`'s own doc
/// comment. This instead races two fully independent `Task.detached` closures — one reading, one
/// sleeping — and resumes on whichever settles first, leaving a timed-out read running unobserved.
/// That leaked read can, at worst, consume one later snapshot no one is left waiting for; it can
/// never hang this function, matching the tolerance that `RaceBox` precedent already accepts.
///
/// # Why the stream, and not the iterator, crosses into the detached task
///
/// `AsyncStream<SessionSnapshot>.Iterator` is not `Sendable` in this SDK (confirmed by hand — see
/// its stdlib declaration), so it cannot be captured into a `Task.detached` closure, put behind
/// `OSAllocatedUnfairLock`, or returned across an actor boundary; design D14 (`home/PRD.md`)
/// reserves `@unchecked Sendable`/`nonisolated(unsafe)` for the audio ring buffer only, so working
/// around that is not an option here either. `AsyncStream<SessionSnapshot>` itself *is* `Sendable`
/// (also confirmed by hand), so each detached reader task instead makes its own fresh iterator
/// from the shared stream and reads from that — which, per the multiplexing behaviour above, is
/// indistinguishable from continuing any other iterator already reading the same stream.
func waitForSnapshot(
    from stream: AsyncStream<SessionSnapshot>,
    timeout: TimeInterval = 8,
    description: String,
    sourceLocation: SourceLocation = #_sourceLocation,
    where predicate: (SessionSnapshot) -> Bool
) async -> SessionSnapshot? {
    let deadline = Date().addingTimeInterval(timeout)
    while true {
        let remaining = deadline.timeIntervalSinceNow
        guard remaining > 0 else {
            Issue.record("timed out after \(timeout)s waiting for \(description)", sourceLocation: sourceLocation)
            return nil
        }

        switch await raceNextSnapshot(stream, remaining: remaining) {
        case .value(let snapshot):
            if predicate(snapshot) {
                return snapshot
            }
        case .streamEnded:
            Issue.record("the update stream ended while waiting for \(description)", sourceLocation: sourceLocation)
            return nil
        case .timedOut:
            Issue.record("timed out after \(timeout)s waiting for \(description)", sourceLocation: sourceLocation)
            return nil
        }
    }
}

private enum SnapshotWaitOutcome {
    case value(SessionSnapshot)
    case streamEnded
    case timedOut
}

/// Races one read from `stream` against `remaining` seconds using two independent
/// `Task.detached` closures, resuming a single continuation with whichever settles first — see
/// `waitForSnapshot`'s own doc comment for why a structured race (`.timeLimit`, `withTaskGroup`)
/// cannot bound this instead, and why the reader task makes its own iterator from `stream` rather
/// than receiving one.
private func raceNextSnapshot(
    _ stream: AsyncStream<SessionSnapshot>,
    remaining: TimeInterval
) async -> SnapshotWaitOutcome {
    let raceBox = SnapshotRaceBox()

    return await withCheckedContinuation { (continuation: CheckedContinuation<SnapshotWaitOutcome, Never>) in
        Task.detached {
            var iterator = stream.makeAsyncIterator()
            let snapshot = await iterator.next()
            let outcome: SnapshotWaitOutcome = snapshot.map(SnapshotWaitOutcome.value) ?? .streamEnded
            await raceBox.resolve(outcome, continuation)
        }
        Task.detached {
            try? await Task.sleep(for: .seconds(max(remaining, 0.001)))
            await raceBox.resolve(.timedOut, continuation)
        }
    }
}

/// Resumes a single `CheckedContinuation` with whichever of two competing `Task.detached`
/// closures calls `resolve` first, ignoring the second call — the same technique
/// `HostSupervisorChildOutputTests.RaceBox` uses, generalised to a three-way outcome.
private actor SnapshotRaceBox {
    private var settled = false

    func resolve(_ outcome: SnapshotWaitOutcome, _ continuation: CheckedContinuation<SnapshotWaitOutcome, Never>) {
        guard !settled else { return }
        settled = true
        continuation.resume(returning: outcome)
    }
}
