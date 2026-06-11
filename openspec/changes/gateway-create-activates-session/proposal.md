## Why

The `Dmon.Gateway` WebSocket gateway's `create` handshake is broken end-to-end: the first
real client (`dmon-swift`) cannot open a session. The gateway drives `session.create`
followed by a path-less `session.load` ("load the session we just created"), but the core's
`SessionHandler.CreateAsync` persists the session and emits `createResult` **without setting
`_currentSession`**. The path-less `session.load` then finds no active session, fails with
`commandError code=noSessionIdOrPath`, and the gateway closes the socket with WebSocket close
code `4500 "session create failed"`. No client ever receives `created`.

## What Changes

- The agent core's `session.create` SHALL make the just-created session the **active**
  session before emitting `session.createResult`, so a subsequent path-less `session.load`
  resolves it. This is decision (a): `CreateAsync` sets `_currentSession = meta` only — it
  does **not** acquire the session lock; the gateway's follow-up `session.load` still
  acquires the lock and rehydrates. The two-step gateway handshake is preserved unchanged.
- Add a regression test asserting that a fresh `session.create` followed by a path-less
  `session.load` succeeds with `session.loadResult` (the exact path the gateway exercises).
- No wire-protocol shape, command, or event changes. No gateway code change.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `agent-core`: add a requirement that a freshly created session becomes the active session,
  so a path-less `session.load` immediately after `session.create` resolves the new session
  rather than failing with `noSessionIdOrPath`.

## Impact

- **Code:** `src/Dmon.Core/Rpc/SessionHandler.cs` — `CreateAsync` sets `_currentSession`.
- **Tests:** `test/Dmon.Core.Tests` — new SessionHandler test for create-then-pathless-load.
- **Unblocks:** the `Dmon.Gateway` `create` control frame (spec `remote-session-gateway`,
  "Gateway session-create control frame") and the `dmon-swift` client's first handshake.
- **No impact:** wire protocol, gateway code, session on-disk layout, other commands.
