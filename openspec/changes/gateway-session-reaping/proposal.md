## Why

The `Dmon.Network` gateway has two session-reaping leak paths (repo audit 2026-07-06, re-verified present on `main@1ecdcf9`). The `SessionReaper` decides reapability **solely** on a handler's `_detachedAt` grace-clock timestamp (`SessionReaper.cs:79-88`), but two code paths leave a handler dead-yet-un-armed (`_detachedAt == null`), so the reaper skips it forever:

1. **Drain-failure leak.** `SessionHandler.DrainAsync`'s catch block (`SessionHandler.cs:595-605`) sets `_connection = null` on a send/pump failure **without** arming `_detachedAt`. The endpoint forwarding loop's `finally` → `handler.Detach(connection)` (`NetworkConnectionEndpoint.cs:276,283`) is then a no-op, because `Detach` only arms `_detachedAt` under the identity guard `ReferenceEquals(_connection, connection)` (`SessionHandler.cs:289`), which fails once the drain path already nulled `_connection`. A broken connection thus orphans the handler with `_connection == null && _detachedAt == null` — invisible to the reaper.

2. **Created-but-never-attached leak.** `HandleCreateAsync` (`NetworkConnectionEndpoint.cs:299-406`) spawns the core, registers the handler, and replies `created`, but never calls `Attach`. Since `_detachedAt` is only ever set by `Detach` (which requires a prior `Attach`), a client that receives `created` and never sends the follow-up `attach` frame leaks the handler with no grace clock ever armed.

Each leaked handler retains its child `dmoncore` process (`_coreSession`) and its unbounded, never-truncated per-session ADR-014 event-replay buffer (`_seqLog`, `SessionHandler.cs:59`). On a long-running single-tenant home server these accumulate until the machine is out of processes/memory. Both leaks contradict the *existing* intent of ADR-012 Decision 7/8 ("the grace timer starts on any **detected** disconnect") and ADR-014 ("the buffer is reaped with the handler") — the ADRs assume this cannot happen; the code does not enforce it.

## What Changes

- **Arm the grace clock on drain-failure.** The `DrainAsync` failure path (and any path that clears `_connection` on a detected disconnect) SHALL arm `_detachedAt` so a broken connection always becomes reapable under the running-turn-aware TTL, exactly as an orderly `Detach` does. The fix must be idempotent with the subsequent no-op `Detach` and must not disturb a live re-attach that installed a newer connection (preserve the identity-guard semantics).
- **Make created-but-never-attached handlers reapable.** A handler that has been created/registered but never attached SHALL be reapable after a grace TTL, with the reap clock cleared on first `Attach`. On reap it tears down the core and buffer like any other idle handler.
- **Regression tests.** Add coverage for both leak paths — neither is currently tested (the existing send-failure test re-attaches immediately and never asserts `DetachedAt`).

No wire-protocol, RPC-shape, or public-verb changes. The `created` → `attach` client contract is unchanged; only the server-side reap safety net is added.

## Capabilities

### New Capabilities

_None — this is a correctness/resilience fix to existing gateway session lifecycle behavior._

### Modified Capabilities

- `remote-session-gateway`: **MODIFIED** — the "Running-turn-aware detached lifetime" / heartbeat requirement is tightened so the grace timer is armed on **every** detected disconnect (including a drain/send failure that clears the connection), and a **created-but-never-attached** handler becomes reapable after a grace TTL (cleared on first attach), so no detected-dead or never-attached handler can escape the reaper.

## Impact

- **Code:** `frontends/Dmon.Network/Sessions/SessionHandler.cs` (drain-failure arm + never-attached reap clock), `frontends/Dmon.Network/Sessions/SessionReaper.cs` (honour the never-attached clock if a distinct field is used), `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` (create path), `frontends/Dmon.Network/Sessions/SessionRegistry.cs` (only if the reap query needs it).
- **Tests:** `test/Dmon.Network.Tests/HeartbeatAndReaperTests.cs`, `test/Dmon.Network.Tests/SessionHandlerTests.cs`.
- **ADRs:** conforms to ADR-012 Decision 7/8 and ADR-014 as written — **no amending/superseding ADR expected**. If implementation reveals the created-never-attached reap needs a lifecycle distinction the ADRs don't cover, that is a stop-and-ask.
- **No impact:** wire protocol, RPC framing, session storage, providers, the agent core, other frontends, author-facing composition verbs.
