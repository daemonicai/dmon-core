## ADDED Requirements

### Requirement: Per-session profile sourced from the persisted session record
The per-session `profile` selection SHALL be sourced from the persisted session record (`meta.json`) rather than from a transient in-memory value. The profile name supplied to `IAgentProfileResolver` for a session SHALL be the `profile` stored in that session's record at creation; a session with no stored profile SHALL supply a null `requestedProfile`, preserving fallback to `defaultProfile` or the built-in `coding` profile. The resolution input SHALL NOT be hardcoded to null.

#### Scenario: Resolver receives the persisted profile
- **WHEN** a turn begins for a session whose record stores `profile` = `"researcher"`
- **THEN** the resolver is invoked with `requestedProfile` = `"researcher"` and the session runs under the `researcher` profile

#### Scenario: Stored profile survives a reload-and-resume
- **WHEN** a session created under `profile` = `"researcher"` is reloaded in a fresh process and a turn begins
- **THEN** the resolver is invoked with `requestedProfile` = `"researcher"`, without the profile being re-supplied at reload

#### Scenario: No stored profile resolves to default
- **WHEN** a turn begins for a session whose record stores no profile
- **THEN** the resolver is invoked with a null `requestedProfile` and the session falls back to `defaultProfile` or the built-in `coding` profile
