## ADDED Requirements

### Requirement: Per-session profile travels the session-create surface
The ADR-003 session-create command SHALL carry an optional per-session `profile` field. When present, the core SHALL pass it as the `requestedProfile` to `AgentProfileContext.EnsureResolvedAsync` so the resolved `AgentProfile` (persona, asset flag, permission mode) governs that session, satisfying the `agent-profiles` *Profile selection* and *Session-scoped single-sourced resolution* requirements. When the field is absent or null, resolution SHALL fall back to `defaultProfile` then the built-in `coding` profile exactly as today, so existing clients are unaffected. The field SHALL be optional and backward-compatible (omitting it MUST NOT change the wire-compatibility of `session.create`).

#### Scenario: Create command carries a profile
- **WHEN** a session-create command includes `profile: "<name>"` for a profile in the effective set
- **THEN** the core resolves the session's profile to `<name>` and the same `AgentProfile` instance feeds the system-prompt builder, asset provisioning, and the permission gate

#### Scenario: Create command omits the profile
- **WHEN** a session-create command carries no `profile` field
- **THEN** the core resolves the profile from `defaultProfile`, falling back to the built-in `coding` profile, identical to the pre-change behaviour

### Requirement: Profile-selecting session creation
Session creation SHALL allocate a `sessionId`, select an agent profile (the `agent-profiles` capability — persona, asset directory, permission mode), provision the per-session storage directory (ADR-004), and spawn the `SessionHandler` whose core runs under that profile. A requested profile that does not exist SHALL fail session creation with an actionable error.

#### Scenario: Session created under a profile
- **WHEN** a client creates a session specifying a profile in the effective set
- **THEN** the gateway spawns a handler whose core runs under that profile and returns the new `sessionId`

#### Scenario: Unknown profile fails creation
- **WHEN** session creation requests a profile that is not in the effective set
- **THEN** creation fails with an actionable error, no `dmoncore` process is spawned, and no handler is registered

### Requirement: Session creation honours the concurrent-handler cap
Gateway session creation SHALL register the new handler through the cap-aware registration primitive (`SessionRegistry.TryRegister`) so the configured `MaxConcurrentHandlers` ceiling is enforced. When the cap is already reached, creation SHALL be rejected with an actionable error and SHALL NOT spawn a `dmoncore` process or leave an orphaned handler. Re-attaching to an existing session SHALL remain cap-free (it adds no handler).

#### Scenario: Creation rejected at the cap
- **WHEN** a client requests a new session while the number of live handlers already equals `MaxConcurrentHandlers`
- **THEN** creation is rejected with an actionable error and no new core process or handler is created

#### Scenario: Creation admitted below the cap
- **WHEN** a client requests a new session while live handlers are below `MaxConcurrentHandlers`
- **THEN** the handler is registered and counts toward the cap for subsequent creation requests

### Requirement: Failed creation leaves no residue
A session creation that fails — unknown profile, cap reached, storage-provisioning error, or core spawn failure — SHALL leave no partially-created session: no registered handler, no orphaned `dmoncore` process, and no half-initialised session directory that would be mistaken for a usable session.

#### Scenario: Spawn failure is cleaned up
- **WHEN** profile resolution succeeds but spawning or initialising the core fails
- **THEN** the gateway reports an actionable error and removes any handler, process, or storage artefacts created during the attempt
