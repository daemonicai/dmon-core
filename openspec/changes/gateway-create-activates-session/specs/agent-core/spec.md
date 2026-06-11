## ADDED Requirements

### Requirement: A freshly created session becomes the active session

On `session.create`, the agent core SHALL set the just-created session as the core's active (current) session after persisting the session directory and before emitting `session.createResult`. Setting the active session SHALL update the in-memory current-session reference only; it SHALL NOT acquire the session lock — lock acquisition and conversation rehydration remain the responsibility of `session.load`. As a consequence, a `session.load` issued with no `path` immediately after `session.create` SHALL resolve to the just-created session and SHALL complete with `session.loadResult`, rather than failing with `commandError code=noSessionIdOrPath`. This matches the gateway's two-step create handshake (`session.create` → path-less `session.load`).

#### Scenario: Path-less load after create resolves the new session

- **WHEN** the core handles `session.create` (with or without a profile) and then receives a
  `session.load` carrying no `path` and no prior active session
- **THEN** the create emits `session.createResult` with the new session, the path-less load
  resolves that same session, acquires its lock, rehydrates it, and emits `session.loadResult`
  for the same session id — no `commandError` is emitted

#### Scenario: Create does not lock the session

- **WHEN** the core handles `session.create`
- **THEN** the current session is set to the new session but the session lock is **not** held
  by `create`; the lock is acquired by the subsequent `session.load`
