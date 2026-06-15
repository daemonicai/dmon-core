## ADDED Requirements

### Requirement: Session agent selector replaces the profile field

A session is created against a named **agent** — a `.cs` composition root (ADR-022 Decision 14) — not a profile. The session-create command, the `create`/`created` gateway control frames, and the persisted `SessionMeta` SHALL identify the selected agent by an `agent` field (the agent name), replacing the former `profile` field. The `agent` field SHALL appear in the machine-readable wire-protocol schema export wherever `profile` previously appeared, and the freshness gate SHALL fail if the exported schema still carries a `profile` field on these DTOs. This is a wire-shape change with no back-compat shim (no production deployments).

#### Scenario: Create command carries an agent selector

- **WHEN** the exported wire-protocol schema is regenerated for the session-create command and the `create` control frame
- **THEN** each exposes an `agent` field (string, the agent name) and no `profile` field

#### Scenario: Persisted session record names its agent

- **WHEN** a session is created and its `SessionMeta` is persisted
- **THEN** the record stores the selected `agent` name and contains no `profile` field

#### Scenario: Freshness gate rejects a stale profile field

- **WHEN** the checked-in schema is verified against the current DTOs after the rename
- **THEN** the freshness gate fails if any session-create command, control frame, or `SessionMeta` in the exported schema still declares a `profile` field
