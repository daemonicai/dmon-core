## Context

`Dmon.Gateway` opens a fresh session by driving the core through a two-step handshake in
`GatewayConnectionEndpoint.DriveSessionHandshakeAsync`:

1. `session.create` (id `gw-session-create`, optional profile) → expects `session.createResult`.
2. `session.load` with **no path** (id `gw-session-load`) → expects `session.loadResult`.
   Its comment states the intent: "no path — loads the session we just created in its default
   location."

In `src/Dmon.Core/Rpc/SessionHandler.cs`, `CreateAsync` (line 30) calls
`_store.CreateAsync(...)` and emits `SessionCreatedResultEvent`, but never assigns
`_currentSession`. `LoadAsync` (line 93) with `cmd.Path == null` falls through to the
`_currentSession == null` branch and emits `commandError code=noSessionIdOrPath`. The gateway
treats that as a handshake failure and closes the socket with code `4500`. The gateway has
never had a live client, so this regressed silently until `dmon-swift` exercised it.

This is a core-side gateway↔core handshake bug, not a client bug.

## Goals / Non-Goals

**Goals:**
- A path-less `session.load` immediately after `session.create` resolves the new session and
  completes with `session.loadResult`.
- Smallest viable change; preserve the existing gateway handshake (`create` → path-less `load`)
  byte-for-byte. No gateway code change.
- A regression test covering the exact create-then-path-less-load sequence.

**Non-Goals:**
- Changing the wire protocol, commands, or events (no shape changes).
- Making `session.create` fully self-sufficient (acquiring the lock + rehydrating) — see the
  rejected alternative below.
- Any change to session on-disk layout, fork/clone, or other handlers.

## Decisions

### Decision: `CreateAsync` sets `_currentSession` only (decision (a))

`CreateAsync` assigns `_currentSession = meta` after `_store.CreateAsync(...)` returns and
before emitting `session.createResult`. It does **not** acquire the session lock and does not
rehydrate conversation history. The gateway's follow-up path-less `session.load` then takes the
existing `_currentSession is not null` branch in `LoadAsync`, derives the session id, acquires
the lock, calls `_store.LoadAsync`, and emits `session.loadResult` — exactly the existing code
path, now reachable.

**Why over the alternative:** "the session just created is the current session" is the
least-surprising semantics and mirrors what `LoadAsync` already does on success
(`_currentSession = meta`). It is a one-line change confined to `CreateAsync`, and it keeps lock
acquisition in exactly one place (`LoadAsync`), avoiding a create-acquires / load-re-acquires
double-lock dance.

**Alternative considered — decision (b), fully activate on create:** `CreateAsync` acquires the
lock and rehydrates so `create` alone is sufficient. Rejected: larger change, duplicates lock +
load work because the gateway still issues the follow-up `load` (which would release and
re-acquire the lock and reload), and spreads lock ownership across two handlers for no benefit to
the current handshake.

**Alternative considered — gateway-side fix:** pass the created session's directory as
`SessionLoadCommand.Path`. Rejected (per the fix brief): the gateway's intent ("load the session
we just created") is already explicit, and a created session being the current session is the
least-surprising core semantics. Fixing it core-side keeps the gateway forwarding-only.

## Risks / Trade-offs

- **A subsequent path-less `session.load` with no preceding `create` still fails** → Intended.
  Decision (a) only sets `_currentSession` on create; the `noSessionIdOrPath` error remains
  correct when there is genuinely no active session and no path. The spec scenario is scoped to
  the post-create case.
- **`create` now mutates handler state (`_currentSession`) without holding the lock** → Low risk:
  the core is single-session per process and commands are processed by one dispatch loop; the
  lock is still acquired by `load` before any turn runs. No concurrent writer is introduced.
- **Test must exercise the real handler path, not just the store** → The regression test drives
  `SessionHandler.CreateAsync` then `LoadAsync` (path-less) against a temp store and asserts a
  `SessionLoadedResultEvent` (not a `CommandErrorEvent`) for the created session id.
