# A `turn.submit` can complete with no event on the wire at all

**Status:** open — mechanism **verified by source trace** (three independent reads); **never observed at runtime**
**Where:** `core/Dmon.Core/Rpc/TurnHandler.cs:112-158` and `:291`; `core/Dmon.Core/Rpc/CommandDispatcher.cs:83-86`; `core/Dmon.Core/Rpc/EventEmitter.cs:17-31`
**Surfaced:** 2026-08-07, during `dmon-home-foundations` section 7 (block B4), while documenting which events a Swift client may rely on to terminate a rendered turn
**Severity:** low likelihood, **unbounded consequence for a client** — the failure mode is a UI that waits forever with no way to tell a wedged turn from a slow one

## The mechanism

Every other outcome of a `turn.submit` puts *something* on the wire — `turnEnd` on success or
cancellation, an `error` event on a rejected or failed turn. **This path puts nothing on it.**

`TurnHandler.SubmitAsync`'s `try` has a `finally` and **no `catch`**. Before `RunTurnAsync` is
reached — and therefore before `TurnStartEvent` is emitted at `:291` — it runs asset
provisioning and `await _systemPromptBuilder.BuildAsync(_turnCts.Token)`. If `_turnCts` is
cancelled during that window the `OperationCanceledException` escapes `SubmitAsync` entirely
and lands in `CommandDispatcher.RunGuardedAsync`'s `catch (OperationCanceledException) { }`
(`:83-86`), which **swallows it silently**. No `error`, no `turnStart`, no `turnEnd`.

The window is **not** confined to the first turn, which is the easy thing to get wrong here.
`EventEmitter.EmitAsync` takes the same token and begins with
`await _gate.WaitAsync(cancellationToken)`; `SemaphoreSlim.WaitAsync` throws immediately on an
already-cancelled token, free semaphore or not. `TurnStartEvent`'s own emit passes the real
`_turnCts.Token` and sits **outside** the `while(true)` loop's `catch (OperationCanceledException)`.
So the true boundary is **"any cancellation observed before `TurnStartEvent` reaches the wire"**,
which comprises the first-turn provisioning window *and*, on every turn, a narrow race against
the `turnStart` emit itself.

## How it is reached

`TurnAbortCommand` dispatches **inline** through `RunGuardedAsync`, independent of the
background task running the submit, and `AbortAsync` cancels the same `_turnCts`
(`TurnHandler.cs:174-181`). So a `turn.abort` sent promptly after a `turn.submit` — an ordinary
user action, not a contrived one — reaches the window. On a first turn its width comes from
**local disk I/O**: `SystemPromptBuilder.BuildAsync` (`core/Dmon.Core/SystemPrompt/SystemPromptBuilder.cs:48-79`)
resolves `~/.dmon/AGENTS.md`, `./AGENTS.md` and `./CLAUDE.md` from disk and reads
`GetCurrentConfig()`, a synchronous lookup. **There is no network call and no model-provider
round trip on that path** — provider resolution (`GetCurrentAsync`) runs later, inside
`RunTurnAsync`, after `turnStart`. On later turns the window narrows to microseconds.

## Why it matters more to a client than it looks

A client cannot distinguish "this submit produced nothing and never will" from "this turn is
slow". `dmon-home`'s `TurnProjection` (`Sources/GatewayClient/TurnProjection.swift`, in the separate `daemonicai/dmon-home` repository) maps
the wire to four terminal outcomes, and its doc comment names this case explicitly as the one
where **no** terminal `TurnEvent` ever arrives. Any renderer that finalises purely on a terminal
event will wait indefinitely. That is a client-side mitigation for a host-side gap.

Note the asymmetry that makes this worth fixing at the source: the host **knows** the submit is
over — it is in a `catch` — and chooses to say nothing.

## What to do

Emit something on that path. The narrow fix is for `RunGuardedAsync`'s cancellation catch to
emit an `ErrorEvent` (recoverable, distinguishable from `internalError`) rather than swallowing,
or for `SubmitAsync` to catch `OperationCanceledException` itself and emit a cancellation event —
symmetric with the in-loop cancellation path, which already reaches `turnEnd`. Either makes the
four outcomes exhaustive *and* observable.

Whichever is chosen, it is a **protocol-visible change** — a client would newly receive an event
where it previously received none — so it wants a spec delta, not a quiet fix.

## Provenance

Found by the `reviewer` agent across two rounds of B4's comment review, each round narrowing a
scope claim the previous one had stated too confidently: first that an `error` event arrives
*instead of* the lifecycle events (false — it can arrive mid-stream after `turnStart` and several
deltas), then that the silent window is first-turn-only (false — see above). **Verified by source
trace only. Not reproduced at runtime, and no test covers it.** The outcome space was
subsequently argued closed at four shapes — `turnInProgress` alone, mid-turn `internalError`,
cancellation reaching `turnEnd`, and this silence — on the grounds that every path bottoms out in
the turn gate, the loop's cancellation catch, an exception escaping the loop, or an exception
escaping everything before `turnStart`.

Out of scope for `dmon-home-foundations`, which adds no .NET behaviour.
