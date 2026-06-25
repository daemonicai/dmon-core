## MODIFIED Requirements

### Requirement: Network session-create control frame
The network host WebSocket surface SHALL accept a `create` control frame carrying an optional `agent`, in addition to the existing `attach` frame. On a successful create the network host SHALL spawn a `dmoncore` process, drive it to create a session under the requested agent, register the resulting `SessionHandler`, and reply with a typed `created` frame carrying the new `sessionId` (ADR-015 — a typed, correlated result, not a generic response envelope). The `agent` value SHALL name a `.cs` composition root resolved under the network host's configured workspace root and SHALL NOT be a client-supplied path. The client SHALL then `attach` to that `sessionId` through the existing attach flow. A create frame SHALL be valid as a first frame on a connection, alongside `attach`.

#### Scenario: Create spawns an agent-bound session
- **WHEN** a client sends `create {agent: "researcher"}` and `researcher` resolves to a `.cs` composition root under the configured workspace root
- **THEN** the network host spawns a core, creates a session whose record stores `agent` = `"researcher"`, registers the handler, and replies `created {sessionId}`

#### Scenario: Create without an agent
- **WHEN** a client sends `create {}` with no agent
- **THEN** the network host spawns a core, creates a session with no stored agent (resolving to the default `.cs` composition root), registers the handler, and replies `created {sessionId}`

#### Scenario: Client attaches to the created session
- **WHEN** the client has received `created {sessionId}`
- **THEN** sending `attach {sessionId, lastSeq: 0}` attaches to the spawned handler through the existing attach flow
