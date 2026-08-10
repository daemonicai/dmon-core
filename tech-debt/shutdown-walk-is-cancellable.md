# The supervisor's shutdown walk is cancellable by construction

**Status:** open
**Where:** `home/Sources/Supervisor/HostSupervisor.swift` — `shutdown()`'s `for` loop; `HostRuntime.shutdownForTermination()`
**Surfaced:** 2026-08-04, by the section-4 supervisor (which also corrected its own first framing of it)
**Severity:** low today, high if disturbed — the failure mode is silent

## What

`shutdown()`'s soundness now rests on its `for` loop always walking every child.
That is true today only because the loop contains no cancellation checkpoint.

**Cancellation itself is safe.** Both `withTimeout` outcomes reach a group
`SIGKILL`, so cancelling degrades to "kill everything fast" rather than "skip
the kill". The exposure is narrower: someone adding `try Task.checkCancellation()`
inside that loop, or rewriting it into a throwing form that bails, means
children later in the reverse order are **never signalled at all — with no
compiler or test signal**, because the deleted process-group sweep iterated
`states.values` rather than following the loop and was the only thing that would
have caught it.

## Why it matters

The safety property is enforced by a comment, not by a type or a test. That is
exactly the shape this project keeps getting bitten by.

## What to do

One line: wrap `await supervisor.shutdown()` in `HostRuntime.shutdownForTermination()`
in an unstructured `Task { }.value`, making the walk uncancellable by
construction. That pattern is already used in this file for the shielded reap,
so it is not a new idea.

Optionally with a test that cancels `shutdownForTermination()` immediately and
asserts a `trap '' TERM` grandchild still dies.

## Related, same file, same class

**The graceful branch under cancellation skips the reap and the state clear.**
`handleExit` never runs, so `spawnedChild` stays populated, `.stoppedIntentionally`
is never published, and the `SIGKILL`ed leader is never reaped. Harmless at app
exit — the process is going away — but it **matters the moment
`shutdownForTermination()` gains a non-exit caller**, which section 8 is the
first plausible place for.

`AppDelegate`'s doc comment carries the corrected scoping of all of this.
