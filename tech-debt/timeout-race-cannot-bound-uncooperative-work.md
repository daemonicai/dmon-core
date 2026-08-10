# `Supervisor`'s `withTimeout` cannot bound an operation that ignores cancellation

**Status:** open — mechanism **proven by executable repro**; blast radius **not audited**
**Where:** `home/Sources/Supervisor/TimeoutRace.swift`; call sites `home/Sources/Supervisor/HealthChecker.swift:50` and `home/Sources/Supervisor/HostSupervisor.swift:787`
**Surfaced:** 2026-08-06, during `dmon-home-foundations` section 6 (block B3), while reviewing a *copy* of this helper made for `GatewayClient`
**Severity:** unknown and worth establishing — the helper is used on shutdown paths that sections 4 and 5 spent rounds hardening

## The mechanism

`withTimeout` races the operation against a sleeping task inside a `withTaskGroup`, then
calls `group.cancelAll()` when one wins. **A task group cannot exit its scope while any of
its children is still running**, cancelled or not — cancellation in Swift is cooperative,
and cancelling a child that never checks it, or that is parked inside a call which does not
honour it, does not make it finish. So when the raced operation is exactly the kind that
cannot be interrupted, `withTimeout` does not return either: the timeout is a no-op and the
caller's bound is illusory.

The doc comments claim the opposite — that a losing operation "keeps running unobserved" and
the caller "gets its bound back regardless". **That is the part that is false**, and it is
the sentence a reader relies on when deciding this helper is safe to wrap around something
uninterruptible.

## Evidence

Run during the B3 review, outside the repo, then deleted:

- `withTimeout(1.0) { <a continuation that is never resumed> }` — **did not return within 5
  seconds**. Hung indefinitely.
- The same never-resuming operation, raced by **two unstructured `Task`s resuming a shared
  `CheckedContinuation`** instead of two children of one group — **returned at ~1.0s**.

So the defect is specific to the structured-group shape, and a correct bound is achievable
without abandoning the approach.

## Why it may or may not bite here

It bites only where the raced operation can fail to honour cancellation. **The two call
sites were not audited** — that is the work this note is asking for, and the reason its
severity is "unknown" rather than "low". `HealthChecker` races a health probe and
`HostSupervisor` races process teardown; whether either can park somewhere uninterruptible
is exactly the question.

Note the asymmetry that makes this worth checking rather than assuming: if these operations
*do* honour cancellation, the helper works and only its comment is wrong. If either does
not, a supervisor shutdown can hang — and the shutdown path is the one sections 4 and 5
repeatedly found to be where this codebase's defects hide, because nothing downstream of a
hang ever reports it.

## What to do

1. **Correct the doc comments regardless of the audit.** They currently license exactly the
   misuse the mechanism cannot survive.
2. **Audit both call sites** for whether their operation can park uninterruptibly.
3. **If a real bound is needed**, the verified shape is the unstructured-task race above —
   or remove the need for one. `GatewayClient` took the second route for its own copy of
   this problem: rather than bounding a join on a read loop that might never wake, the actor
   ends the consumer-facing stream itself and orphans the loop, which is deterministic with
   no timing at all. See `GatewayConnection.close()` and
   [websocket-receive-cancellation-leak](websocket-receive-cancellation-leak.md).

## Provenance

Found by the `reviewer` during section 6 block B3 of `dmon-home-foundations`, reviewing the
`GatewayClient` copy of this helper; the mechanism was **verified by running it**, not
inferred from reading. The pre-existing `Supervisor` original was outside that block's scope,
so the call-site audit was deliberately **not** attempted — do not read this note as saying
the supervisor is known to hang.
