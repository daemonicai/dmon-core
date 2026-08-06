# A closed `WebSocketGatewayTransport` can leave its read loop running forever

**Status:** open — contained, deliberately deferred; the containment is tested, the residual is not fixable in this module
**Where:** `home/Sources/GatewayClient/WebSocketGatewayTransport.swift` (`close()`), `home/Sources/GatewayClient/GatewayConnection.swift` (`close()`, `runReadLoop`)
**Surfaced:** 2026-08-06, during `dmon-home-foundations` section 6 block B3
**Severity:** low per occurrence, unbounded in aggregate — one leaked task and one stuck continuation **per reconnect**, on the live conformer only

## What

`GatewayConnection.close()` closes the transport, cancels the read-loop task, drops its
reference and finishes the consumer-facing stream itself. It does **not** wait for the read
loop to exit, and that is deliberate — see below. The consequence is that the loop may still
be parked inside `URLSessionWebSocketTask.receive()`, and nothing guarantees it ever leaves:
cancelling that task is **reported not to reliably complete or throw from a pending async
`receive()`**, and `Task` cancellation does not help because that async bridge is not wired
to cooperative cancellation.

So after a close, on a real socket, the read-loop `Task` and its suspended continuation can
remain alive indefinitely, holding the transport actor, against a connection that is already
closed. Every reconnect can add another.

## What is *not* wrong

Worth stating, because the shape invites the wrong conclusion:

- **`close()` cannot hang.** It has no suspension point at all — verified by tracing every
  operation in it, and pinned by a test that wedges the loop in a `receive()` which is never
  woken (`InMemoryGatewayTransport(uncooperative: true)`) and asserts `close()` still returns.
- **Consumers cannot hang either.** `close()` finishes the stream itself rather than leaving
  that to the loop, so an iterating consumer terminates even when the loop is wedged forever.
  This was the failure the first two attempted fixes missed.

The leak is what remains once both of those are closed. It is a resource residual, not a
liveness bug.

## Why it is deferred rather than fixed

The documented workaround is the closure-based `receive(completionHandler:)` API, whose
completion handler *is* invoked on cancellation. Adopting it was **deliberately declined**
for now: there is no live socket in this test suite by design, so switching would replace one
untestable claim with another on the strength of a report neither the architect nor the
reviewer could verify from here. The bounded-and-tested containment above holds regardless of
whether that report is accurate, which is why it was preferred.

That reasoning has an expiry: it is an argument for not *guessing*, not an argument against
fixing. Once there is any way to exercise a real socket — a live-transport smoke test, a
loopback `ndmon` in CI, or a manual verification recipe — the closure-based switch becomes
testable and should be reconsidered on its merits.

## What to do

1. **Establish whether it actually leaks in practice**, against a real `ndmon`: connect,
   close, repeat, and watch task/continuation counts. The whole thing rests on a reported
   Foundation behaviour that has not been observed in this repo.
2. **If it does**, switch `WebSocketGatewayTransport.receive()` to
   `receive(completionHandler:)` bridged through a continuation, and verify the pending call
   completes on cancel — with the same live setup, since that is the only thing that can
   verify it.
3. **If it does not**, correct the comments in both files, which currently describe the
   orphaned task as a live possibility.

## Provenance

Found by the `reviewer` during B3, which cited the Apple-acknowledged
`URLSessionWebSocketTask` async-receive cancellation bug. **The bug report was not
independently verified from this repo, and the leak has never been observed here** — what
*was* verified, by running it, is that a group-based timeout cannot bound an uncooperative
operation (see
[timeout-race-cannot-bound-uncooperative-work](timeout-race-cannot-bound-uncooperative-work.md)),
which is what sent the design to its current shape.
