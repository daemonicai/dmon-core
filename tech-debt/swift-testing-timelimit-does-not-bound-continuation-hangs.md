# `.timeLimit` does not bound a hang on an un-cancellable continuation

**Status:** open — **reproduced independently, twice**, once on the real test and once on a minimal isolated package
**Where:** `home/Tests/GatewayClientTests/GatewaySessionTests.swift` — the `.timeLimit(.minutes(1))` traits on `aSecondConcurrentAttachIsRefusedWhileTheFirstIsStillInFlightAndTheFirstStillCompletesCorrectly` and the B5 suspension tests; `Makefile`'s `dmon-home-test` target; `.github/workflows/ci.yml`
**Surfaced:** 2026-08-09, during `dmon-home-foundations` section 7 (block B5), while falsifying a generation-ordering regression test
**Severity:** the mitigation currently in the tree **does not do what its doc comments say it does**

## The claim that turned out to be false

Section 7's B2 added `.timeLimit(.minutes(1))` to a regression test **specifically because that test's failure mode is a hang rather than an assertion**, so that a future regression would fail CI cleanly instead of stalling it. The Architect required it; the doc comment asserts it. Both the requirement and the comment were written **without checking**.

`.timeLimit` is enforced by **cancelling the test's own task** when the deadline elapses. Swift task cancellation is **cooperative** — it sets a flag. It cannot force a suspended `withCheckedContinuation` / `withCheckedThrowingContinuation` to return unless something explicitly resumes it (e.g. via `withTaskCancellationHandler`, which `GatewaySession.attach` does not use — its continuation is a plain `withCheckedThrowingContinuation`). So for exactly the failure mode the trait was added to bound, **it does nothing**.

## Evidence

**Observation 1** (worker, on the real test). With `let generation = pumpGeneration` deliberately moved to before `try await makeTransport()` in `performAttach`, the single affected test parked at negligible CPU for **3 minutes 36 seconds** — 60+× its ~0.01s normal runtime — and had to be killed by hand. The one-minute trait never fired.

**Observation 2** (reviewer, independent, minimal). A from-scratch SwiftPM package outside this repo, one test, the same shape — an unstructured `Task` awaited via `.value`, wrapping a never-resumed `CheckedContinuation`:

```swift
@Test(.timeLimit(.minutes(1)))
func hangsOnLeakedContinuationInsideUnstructuredTask() async throws {
    let parked = Task<Void, Never> {
        await withCheckedContinuation { (_: CheckedContinuation<Void, Never>) in
            // never resumed — same shape as a leaked attachWaiter
        }
    }
    _ = await parked.value
}
```

Run under an external 90-second watchdog: **never terminated.** No runner output was ever emitted — not even "Test run started" — and the process was `kill -9`ed at 91 seconds.

## What is actually exposed

Both hang-shaped regression tests in `GatewaySessionTests.swift` carry a trait that cannot bound them:

- **B2's** hang mode is a leaked `attachWaiter` continuation inside a `Task` another line awaits by `.value` — structurally identical to the reproduction above.
- **B5's** suspension tests park on a `GatedTransportFactory` continuation plus the outer `attach` continuation, neither cancellation-aware.

**There is no external guard today.** `Makefile`'s `dmon-home-test` is a bare `swift test --package-path home` with no wrapper, and `.github/workflows/ci.yml` contains no `dmon-home` reference and no `timeout-minutes` at all — consistent with `dmon-home-test` not being wired into CI yet, which is section 10's job. So nothing bites right now; the exposure arrives **the moment section 10 wires it up.**

## What to do

**An external timeout is the only mechanism that can work** — nothing inside the test process can preempt an un-cancellable continuation. Wrap `swift test` in `dmon-home-test`, and/or set `timeout-minutes` on section 10's CI job.

Two practical notes, both checked:

- GitHub Actions' **default** job timeout is 360 minutes. It is a real backstop but far too blunt to serve as "fail fast" — it would burn six hours before failing.
- Neither `timeout` nor `gtimeout` (coreutils) is installed on this machine, so a naive `timeout 300 swift test …` in the `Makefile` is **not** a zero-dependency fix. It needs that dependency or a small watchdog script.

**Also fix the two doc comments**, which currently assert a protection that does not exist. B5's new comment is already accurate — it says the tests are *"not proven to fail loudly, only to fail visibly-if-watched"*. B2's is not, and was left untouched deliberately as out of that block's scope.

## Provenance

Found by falsifying a regression test rather than by reading — the worker broke the code the test guards, expecting a bounded failure, and got an unbounded hang instead. **Both observations are runtime reproductions, not source-trace inferences.** The generalisation to B2's test is a structural argument from the reproduction's shape, checked against that test's own documented hang mode, but B2's test was not itself re-falsified.

The underlying lesson is this section's recurring one, in its most pointed form: **the mitigation was ordered into the code as a requirement, by the Architect, and asserted in a comment — none of which made it true.**
