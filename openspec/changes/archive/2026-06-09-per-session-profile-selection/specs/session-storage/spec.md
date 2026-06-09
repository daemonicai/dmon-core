## ADDED Requirements

### Requirement: Session record carries the selected profile
The session record SHALL persist the selected agent-profile name in `meta.json` as an optional `profile` field. When a session is created with a `profile`, that name SHALL be written to `meta.json` at creation time. When a session is created without a `profile`, the field SHALL be absent or null, preserving the pre-change default-resolution behaviour. Loading a session SHALL rehydrate the persisted `profile` so it is available for profile resolution without re-supplying it.

#### Scenario: Profile persisted at creation
- **WHEN** a session is created with `profile: "researcher"`
- **THEN** the new session's `meta.json` records `profile` = `"researcher"`

#### Scenario: Absent profile preserves default behaviour
- **WHEN** a session is created with no `profile`
- **THEN** `meta.json` carries no profile name and profile resolution falls back to `defaultProfile` or the built-in `coding` profile

#### Scenario: Profile survives reload
- **WHEN** a session whose `meta.json` records `profile` = `"researcher"` is loaded
- **THEN** the loaded session record exposes `profile` = `"researcher"` without the caller re-supplying it

## MODIFIED Requirements

### Requirement: Session fork and clone
The system SHALL support forking a session at a specific `entryId` and cloning an entire session. A fork or clone SHALL inherit the source session's `profile`, copying it into the new session's `meta.json`; profile inheritance SHALL be the only profile behaviour for fork and clone (no per-operation profile override).

#### Scenario: Fork creates new session from entry point
- **WHEN** the host sends `session.fork {entryId}`
- **THEN** a new session directory is created by copying the source directory, the *new* session's `messages.jsonl` is truncated after the line containing `entryId` (the source file is never mutated), `attachments/` referenced by retained `toolResult` parts are preserved in the copy, and `meta.json` is rewritten with a new id, `parentSession` = source id, `forkEntryId` = `entryId`, and `profile` copied from the source session

#### Scenario: Clone duplicates entire session
- **WHEN** the host sends `session.clone`
- **THEN** a new session is created as an exact copy with a new id, `parentSession` set to the source session id, and `profile` copied from the source session

#### Scenario: Fork of a session with no profile
- **WHEN** the host forks or clones a source session whose `meta.json` carries no profile
- **THEN** the new session's `meta.json` likewise carries no profile and resolves to the default
