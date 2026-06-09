## ADDED Requirements

### Requirement: Gateway session-create control frame
The gateway WebSocket surface SHALL accept a `create` control frame carrying an optional `profile`, in addition to the existing `attach` frame. On a successful create the gateway SHALL spawn a `dmoncore` process, drive it to create a session under the requested profile, register the resulting `SessionHandler`, and reply with a typed `created` frame carrying the new `sessionId` (ADR-015 — a typed, correlated result, not a generic response envelope). The client SHALL then `attach` to that `sessionId` through the existing attach flow. A create frame SHALL be valid as a first frame on a connection, alongside `attach`.

#### Scenario: Create spawns a profile-bound session
- **WHEN** a client sends `create {profile: "researcher"}` and `researcher` is in the effective profile set
- **THEN** the gateway spawns a core, creates a session whose record stores `profile` = `"researcher"`, registers the handler, and replies `created {sessionId}`

#### Scenario: Create without a profile
- **WHEN** a client sends `create {}` with no profile
- **THEN** the gateway spawns a core, creates a session with no stored profile (resolving to the default), registers the handler, and replies `created {sessionId}`

#### Scenario: Client attaches to the created session
- **WHEN** the client has received `created {sessionId}`
- **THEN** sending `attach {sessionId, lastSeq: 0}` attaches to the spawned handler through the existing attach flow

### Requirement: Pre-spawn profile validation rejects unknown profiles
The gateway SHALL validate the requested profile against the effective profile set **before** spawning any core process. A requested profile that is not in the effective set SHALL be rejected with a typed, actionable error naming the unknown profile, and SHALL NOT spawn a core, SHALL NOT register a handler, and SHALL NOT consume a slot against the concurrent-handler cap. Validation at the gateway is an early-rejection convenience; the core's own first-turn resolution remains authoritative.

#### Scenario: Unknown profile rejected without spawning
- **WHEN** a client sends `create {profile: "nope"}` and `nope` is not in the effective profile set
- **THEN** the gateway replies with an actionable error naming `nope`, spawns no core, and registers no handler

#### Scenario: No handler leaked on rejection
- **WHEN** a create is rejected for an unknown profile
- **THEN** the registry handler count is unchanged and no orphaned core process remains

### Requirement: Cap-enforced create registration
The gateway SHALL register a newly created session's handler under the concurrent-handler cap (`MaxConcurrentHandlers`) using the cap-enforcing registration primitive. When the cap is already reached, create SHALL fail with a typed, actionable error and SHALL tear down any core it spawned for that create, leaving no orphaned process and no registry entry. Reattaching to an existing session SHALL NOT be subject to the cap.

#### Scenario: Create rejected at the cap
- **WHEN** the registry already holds `MaxConcurrentHandlers` handlers and a client sends a valid `create`
- **THEN** the gateway replies with an actionable cap error, the spawned core (if any) is torn down, and no new handler is registered

#### Scenario: Reattach is exempt from the cap
- **WHEN** the cap is reached and a client `attach`es to an already-registered session
- **THEN** the attach succeeds, since reattach does not allocate a new handler
