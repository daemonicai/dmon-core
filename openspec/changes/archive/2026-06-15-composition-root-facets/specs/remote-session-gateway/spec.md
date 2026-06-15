# remote-session-gateway Specification

## MODIFIED Requirements

### Requirement: Profile-selecting session creation
Session creation SHALL allocate a `sessionId`, select an **agent** — the name of a `.cs` composition root resolved under the gateway's configured workspace root (ADR-022 D14), never a client-supplied path — provision the per-session storage directory (ADR-004), and spawn the `SessionHandler`. A requested agent that does not resolve to a `.cs` composition root under the configured workspace root SHALL fail session creation with an actionable error.

#### Scenario: Session created under an agent
- **WHEN** a client creates a session specifying an agent
- **THEN** the gateway spawns a handler whose core runs that agent's `.cs` composition root and returns the new `sessionId`

#### Scenario: Unknown agent fails creation
- **WHEN** session creation requests an agent that does not resolve to a `.cs` composition root under the configured workspace root
- **THEN** creation fails with an actionable error and no handler is spawned

### Requirement: Gateway session-create control frame
The gateway WebSocket surface SHALL accept a `create` control frame carrying an optional `agent`, in addition to the existing `attach` frame. On a successful create the gateway SHALL spawn a `dmoncore` process, drive it to create a session under the requested agent, register the resulting `SessionHandler`, and reply with a typed `created` frame carrying the new `sessionId` (ADR-015 — a typed, correlated result, not a generic response envelope). The `agent` value SHALL name a `.cs` composition root resolved under the gateway's configured workspace root and SHALL NOT be a client-supplied path. The client SHALL then `attach` to that `sessionId` through the existing attach flow. A create frame SHALL be valid as a first frame on a connection, alongside `attach`.

#### Scenario: Create spawns an agent-bound session
- **WHEN** a client sends `create {agent: "researcher"}` and `researcher` resolves to a `.cs` composition root under the configured workspace root
- **THEN** the gateway spawns a core, creates a session whose record stores `agent` = `"researcher"`, registers the handler, and replies `created {sessionId}`

#### Scenario: Create without an agent
- **WHEN** a client sends `create {}` with no agent
- **THEN** the gateway spawns a core, creates a session with no stored agent (resolving to the default `.cs` composition root), registers the handler, and replies `created {sessionId}`

#### Scenario: Client attaches to the created session
- **WHEN** the client has received `created {sessionId}`
- **THEN** sending `attach {sessionId, lastSeq: 0}` attaches to the spawned handler through the existing attach flow

### Requirement: Pre-spawn profile validation rejects unknown profiles
The gateway SHALL validate the requested **agent** against the agents resolvable under the configured workspace root **before** spawning any core process. A requested agent that does not resolve to a `.cs` composition root SHALL be rejected with a typed, actionable error naming the unknown agent, and SHALL NOT spawn a core, SHALL NOT register a handler, and SHALL NOT consume a slot against the concurrent-handler cap. The agent name SHALL be resolved under the configured workspace root only; a client-supplied path SHALL NOT be accepted. Validation at the gateway is an early-rejection convenience; the core's own first-turn resolution remains authoritative.

#### Scenario: Unknown agent rejected without spawning
- **WHEN** a client sends `create {agent: "nope"}` and `nope` does not resolve to a `.cs` composition root under the configured workspace root
- **THEN** the gateway replies with an actionable error naming `nope`, spawns no core, and registers no handler

#### Scenario: No handler leaked on rejection
- **WHEN** a create is rejected for an unknown agent
- **THEN** the registry handler count is unchanged and no orphaned core process remains
