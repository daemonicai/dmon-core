## REMOVED Requirements

### Requirement: Agent profile structure

**Reason**: ADR-022 Decision 14 supersedes ADR-013 in full — the "agent profile = persona + assets + permission mode, selected per session" bundle is dissolved into the `.cs` composition root. An agent *is* its composition root; there is no separate named-bundle structure to resolve.

**Migration**: The three elements become builder verbs in the `.cs` root: persona → `UseSystemPrompt(string)` / `IConfiguration["systemPrompt"]` (see the `system-prompt` capability); the `assets` flag → `UseAssets(path)`; the permission mode → `WithPermissionMode` (already present). The fixed-at-session-creation immutability is preserved by the fact that a session runs one composition root.

An agent profile SHALL be a named bundle comprising exactly three elements: a **persona** (the system-prompt identity/norms block), an **`assets` flag** (boolean; default `false`), and a **permission mode** (one of a fixed core enum, `coding` or `sandbox`). A profile SHALL be fixed at session creation and immutable for that session's lifetime.

#### Scenario: Profile carries the three elements
- **WHEN** a profile is resolved for a session
- **THEN** it exposes a persona, an `assets` boolean, and a `permissionMode` of either `coding` or `sandbox`

#### Scenario: Profile is immutable within a session
- **WHEN** a session is active under a resolved profile
- **THEN** the profile cannot be changed for that session, and selecting a different profile requires a new session

### Requirement: Built-in coding profile and default behaviour

**Reason**: ADR-022 Decision 14 — the former built-in `coding` profile is simply the default `Dmon.cs` composition root. There is no profile object to ship or fall back to.

**Migration**: The canonical coding behaviour is the stock scaffolded `Dmon.cs` (the default agent). Its persona moves to that root's `UseSystemPrompt`/`IConfiguration["systemPrompt"]` default; its permission mode to `WithPermissionMode`. "No selection" resolves to the default agent (`Dmon.cs`) rather than a built-in profile name.

The core SHALL ship exactly one built-in profile named `coding`, composed of the canonical D-mon coding persona, `assets: false`, and permission mode `coding`. When no profile is selected by config or session creation, the `coding` profile SHALL apply, and the assembled system prompt and permission behaviour SHALL be identical to the behaviour prior to this change.

#### Scenario: No selection falls back to built-in coding
- **WHEN** a session starts with no `defaultProfile` in config and no per-session `profile` override
- **THEN** the `coding` built-in profile is used

#### Scenario: Default behaviour preserved
- **WHEN** the `coding` profile is active
- **THEN** the persona content equals the prior static core, no asset directory is provisioned, and the permission mode is `coding`

### Requirement: Config-defined profiles with two-scope merge

**Reason**: ADR-022 Decision 14 — the declarative profile subsystem (`ProfilesConfigReader`, `EffectiveProfileSetResolver`, the `profiles:` config map) is deleted. Composition is code, not a config-merged bundle map.

**Migration**: A user/project that needs more than one agent authors additional `.cs` composition roots under `.dmon/agents/<name>.cs` (ADR-022 Decision 14, retaining ADR-020's location and selection). Per-name precedence between scopes is replaced by which `.cs` root the launcher resolves under the workspace root.

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

**Reason**: ADR-022 Decision 14 — per-session selection is preserved but is now a choice of `.cs` composition root by *agent name*, not a profile name resolved against a config-merged set. The gateway `createSession` `profile` parameter becomes `agent`.

**Migration**: `defaultProfile` config and the per-session `profile` parameter are replaced by agent-name selection: a session selects an agent name; the launcher (`ICoreLauncher`) builds and runs `.dmon/agents/<name>.cs`, defaulting to the root `Dmon.cs`. An unknown agent name remains a hard, actionable error at session start. The gateway `createSession` `profile` field is renamed `agent` (see the `remote-session-gateway` capability).

A profile SHALL be selectable via a `defaultProfile` key in `config.yaml` and an optional per-session `profile` parameter at session creation. The per-session parameter SHALL take precedence over `defaultProfile`. A selected profile name that matches no profile in the effective set SHALL produce a hard, actionable error at session start; the system SHALL NOT silently fall back to another profile.

#### Scenario: Per-session override wins
- **WHEN** `defaultProfile` names profile A and session creation passes `profile: B`
- **THEN** profile B is used for that session

#### Scenario: Unknown profile name is a hard error
- **WHEN** a selected profile name matches no profile in the effective set
- **THEN** session start fails with an actionable error naming the unknown profile, and no session is created

### Requirement: Session-scoped single-sourced resolution

**Reason**: ADR-022 Decision 14 — `IAgentProfileResolver` and the `AgentProfile` record are deleted (per the ADR Consequences). There is no resolved profile object; the composition root *is* the single source, evaluated once when the agent is built.

**Migration**: The prompt builder, asset provisioning, and permission gate each draw from the composed values set by the `.cs` root's verbs (`UseSystemPrompt`/config, `UseAssets`, `WithPermissionMode`) rather than from a shared `AgentProfile` instance.

The core SHALL expose an `IAgentProfileResolver` that produces an `AgentProfile` record, resolved exactly once per session. The resolved `AgentProfile` SHALL be the single source consumed by the system-prompt builder, the asset-directory provisioning step, and the permission-mode selection in the permission gate.

#### Scenario: Resolved once per session
- **WHEN** a session starts
- **THEN** the profile is resolved a single time and the same `AgentProfile` instance feeds the prompt builder, asset provisioning, and the permission gate

### Requirement: Per-session asset directory provisioning

**Reason**: ADR-022 Decision 14 — the `assets` flag as a profile element is removed; session assets become a builder verb (`UseAssets`). `ISessionAssetProvisioner` survives, but behind the verb rather than behind a profile flag.

**Migration**: Authors enable per-session assets by calling `UseAssets(path)` in the `.cs` composition root. When the verb is not called, no `assets/<session_id>/` directory is provisioned — equivalent to the former `assets: false`. The provisioning behaviour itself is unchanged.

When the active profile's `assets` flag is `true`, the core SHALL provision a per-session directory `assets/<session_id>/` under the workspace root at session creation. When `assets` is `false`, no such directory SHALL be created. The asset directory SHALL be distinct from the session storage directory's internal `attachments/` and SHALL persist independently of the session's process lifetime.

#### Scenario: Asset directory created under an assets-enabled profile
- **WHEN** a session starts under a profile with `assets: true`
- **THEN** `assets/<session_id>/` is created under the workspace root

#### Scenario: No asset directory under the coding profile
- **WHEN** a session starts under a profile with `assets: false`
- **THEN** no `assets/<session_id>/` directory is created

### Requirement: Per-session profile sourced from the persisted session record

**Reason**: ADR-022 Decision 14 — with profiles dissolved, the persisted per-session selector becomes the `agent` name (formerly `profile`). There is no `IAgentProfileResolver` to feed a `requestedProfile`.

**Migration**: The session record (`meta.json`) stores the selected `agent` name (renamed from `profile`); on reload/resume the launcher resolves the same `.cs` composition root by that name. A session with no stored agent name resolves to the default agent (`Dmon.cs`). The persisted-not-transient and survives-reload guarantees are retained for the `agent` field.

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
