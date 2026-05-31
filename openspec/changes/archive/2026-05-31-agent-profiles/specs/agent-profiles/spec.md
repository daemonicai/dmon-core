## ADDED Requirements

### Requirement: Agent profile structure
An agent profile SHALL be a named bundle comprising exactly three elements: a **persona** (the system-prompt identity/norms block), an **`assets` flag** (boolean; default `false`), and a **permission mode** (one of a fixed core enum, `coding` or `sandbox`). A profile SHALL be fixed at session creation and immutable for that session's lifetime.

#### Scenario: Profile carries the three elements
- **WHEN** a profile is resolved for a session
- **THEN** it exposes a persona, an `assets` boolean, and a `permissionMode` of either `coding` or `sandbox`

#### Scenario: Profile is immutable within a session
- **WHEN** a session is active under a resolved profile
- **THEN** the profile cannot be changed for that session, and selecting a different profile requires a new session

### Requirement: Built-in coding profile and default behaviour
The core SHALL ship exactly one built-in profile named `coding`, composed of the canonical D-mon coding persona, `assets: false`, and permission mode `coding`. When no profile is selected by config or session creation, the `coding` profile SHALL apply, and the assembled system prompt and permission behaviour SHALL be identical to the behaviour prior to this change.

#### Scenario: No selection falls back to built-in coding
- **WHEN** a session starts with no `defaultProfile` in config and no per-session `profile` override
- **THEN** the `coding` built-in profile is used

#### Scenario: Default behaviour preserved
- **WHEN** the `coding` profile is active
- **THEN** the persona content equals the prior static core, no asset directory is provisioned, and the permission mode is `coding`

### Requirement: Config-defined profiles with two-scope merge
Profiles beyond the built-in SHALL be declared in `config.yaml` under a `profiles:` map at the user scope (`~/.dmon`) and the project scope (`./.dmon`). The effective profile set SHALL be the union of both scopes, deduplicated by name, with the project scope winning per-name conflicts — consistent with the ADR-009 extension-set merge. Each profile entry SHALL carry a `persona` (inline text or a `personaFile` path), an `assets` boolean, and a `permissionMode` of `coding` or `sandbox`.

#### Scenario: User and project profiles merged
- **WHEN** both scopes declare profiles under `profiles:`
- **THEN** the effective set is their union deduplicated by name

#### Scenario: Project scope wins a name conflict
- **WHEN** the same profile name is declared at both user and project scope
- **THEN** the project-scope definition is used

#### Scenario: Persona sourced from a file
- **WHEN** a profile entry specifies `personaFile`
- **THEN** the persona content is read from that file rather than from inline `persona` text

### Requirement: Profile selection
A profile SHALL be selectable via a `defaultProfile` key in `config.yaml` and an optional per-session `profile` parameter at session creation. The per-session parameter SHALL take precedence over `defaultProfile`. A selected profile name that matches no profile in the effective set SHALL produce a hard, actionable error at session start; the system SHALL NOT silently fall back to another profile.

#### Scenario: Per-session override wins
- **WHEN** `defaultProfile` names profile A and session creation passes `profile: B`
- **THEN** profile B is used for that session

#### Scenario: Unknown profile name is a hard error
- **WHEN** a selected profile name matches no profile in the effective set
- **THEN** session start fails with an actionable error naming the unknown profile, and no session is created

### Requirement: Session-scoped single-sourced resolution
The core SHALL expose an `IAgentProfileResolver` that produces an `AgentProfile` record, resolved exactly once per session. The resolved `AgentProfile` SHALL be the single source consumed by the system-prompt builder, the asset-directory provisioning step, and the permission-mode selection in the permission gate.

#### Scenario: Resolved once per session
- **WHEN** a session starts
- **THEN** the profile is resolved a single time and the same `AgentProfile` instance feeds the prompt builder, asset provisioning, and the permission gate

### Requirement: Per-session asset directory provisioning
When the active profile's `assets` flag is `true`, the core SHALL provision a per-session directory `assets/<session_id>/` under the workspace root at session creation. When `assets` is `false`, no such directory SHALL be created. The asset directory SHALL be distinct from the session storage directory's internal `attachments/` and SHALL persist independently of the session's process lifetime.

#### Scenario: Asset directory created under an assets-enabled profile
- **WHEN** a session starts under a profile with `assets: true`
- **THEN** `assets/<session_id>/` is created under the workspace root

#### Scenario: No asset directory under the coding profile
- **WHEN** a session starts under a profile with `assets: false`
- **THEN** no `assets/<session_id>/` directory is created
