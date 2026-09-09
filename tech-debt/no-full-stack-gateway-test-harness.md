# No test harness exercises a real gateway over a real spawned core

**Status:** open
**Where:** `test/Dmon.Network.Tests/`
**Surfaced:** 2026-09-09, block 3C of `lazy-session-creation` — reviewer, confirmed by the section-3 and section-5 supervisors
**Severity:** medium — it caps what any gateway task in any change can prove

## What

`Dmon.Network.Tests` has no way to drive a real turn through the gateway. Its
harness backs `NetworkConnectionEndpoint`'s handshake with a `FakeCoreProcess`
(`NetworkCreateE2ETests.cs`) — a bare `ICoreProcess` over a caller-supplied
`TextReader`/`TextWriter` pair, with no `SessionHandler`, no `TurnHandler` and no
session store. Tests script `session.createResult` / `session.loadResult` lines
by hand and assert on what reaches `stdin`.

So a `turn.submit` driven through that harness is a line written to a capturing
writer with nothing behind it to execute. Verified by grep across the whole test
tree: the only **real** `ICoreLauncher` in any test project spawns an actual OS
process (`test/Dmon.Core.Tests/Integration/LiveToolCallE2ETest.cs`).

The three levels that exist are: wire-ordering tests against the fake
(`NetworkCreateE2ETests`, `NetworkCreateFlowTests`), core-level re-enactments of
the handshake's *effect* against a real `SessionHandler` + `SessionStore`, and a
live-process integration test. Nothing joins the first to the third.

## Why it matters

It forces every gateway-touching task to test one level down and say so. Task
`3.6` of `lazy-session-creation` ("the gateway path triggers no implicit
creation") had to be proven by re-enacting the handshake's effect at core level;
what remains on source inspection is that the real `DriveSessionHandshakeAsync`
completes create-then-load before returning. That residue is small and partly
covered by the wire-ordering tests — but it is a residue, and it recurs for the
next change that touches this path rather than being paid down once.

This is **pre-existing infrastructure debt**, explicitly not owed by
`lazy-session-creation`; both its block reviewer and two supervisors agreed it
should be carried rather than fixed there.

## What to do

Provide an `ICoreLauncher` test double that launches an **in-process** core —
real `CommandDispatcher`, `SessionHandler`, `TurnHandler` and `SessionStore` over
a temp directory, with a stub provider — so gateway tests can drive a turn end to
end without spawning a process. The `FakeResolver` pattern from
`test/Dmon.Core.Tests/Rpc/LazySessionCreationRealStackTests.cs` (real store, real
filesystem, isolated only at the directory-resolution seam) is the right shape
for the store half; see [the SpySessionStore note](spy-session-store-weaker-than-fake-resolver.md).

Verified by inspection of the harness and by grep, not by attempting the build.
