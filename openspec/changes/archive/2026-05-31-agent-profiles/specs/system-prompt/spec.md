## MODIFIED Requirements

### Requirement: Static core content
The system prompt SHALL include a **persona** block supplied by the active agent profile (the `agent-profiles` capability) rather than a single compiled-in constant. The built-in `coding` profile's persona SHALL be the canonical content and SHALL:
- Identify the agent as "D-mon" (a coding agent)
- State tool-usage norms: read before editing, prefer targeted edits over full rewrites, ask one short question if scope is genuinely unclear
- State permission model awareness: bash commands and file writes require user confirmation; the runtime handles this
- Use an informal, terse tone with no padding, hedging, or apologies

A non-default profile SHALL replace this persona block in full with its own configured persona; the surrounding scaffolding (dynamic context, project config) is unaffected by which profile is active.

#### Scenario: Persona present in every session
- **WHEN** any session is started regardless of environment or config
- **THEN** the active profile's persona content is present in the system message

#### Scenario: Coding profile persona equals the prior static core
- **WHEN** a session is started under the built-in `coding` profile
- **THEN** the persona content is byte-for-byte the canonical D-mon coding identity that was previously the compiled-in static core

#### Scenario: Non-default profile replaces the persona
- **WHEN** a session is started under a profile whose `persona` differs from the built-in
- **THEN** the system message contains that profile's persona in place of the coding persona, and the dynamic-context and project-config sections are still assembled

### Requirement: Dynamic context assembly
The system prompt SHALL include a dynamic context block assembled at session start containing: working directory (absolute path), OS and platform, active provider name and model ID, and the list of currently loaded extensions (if any). When the active profile has `assets` enabled, the dynamic context block SHALL additionally state the per-session asset directory (`assets/<session_id>/`); when `assets` is disabled, the asset directory SHALL NOT be mentioned.

#### Scenario: Working directory included
- **WHEN** the session starts
- **THEN** the system message includes the absolute path of the process working directory

#### Scenario: Extension list included when extensions are loaded
- **WHEN** one or more extensions are loaded at session start
- **THEN** the system message lists their names in the dynamic context block

#### Scenario: Extension list omitted when none loaded
- **WHEN** no extensions are loaded
- **THEN** the dynamic context block omits the extensions section

#### Scenario: Asset directory surfaced only under an assets-enabled profile
- **WHEN** the active profile has `assets: true`
- **THEN** the dynamic context block states the session's `assets/<session_id>/` directory

#### Scenario: Asset directory omitted under the coding profile
- **WHEN** the active profile has `assets: false`
- **THEN** the dynamic context block makes no mention of an asset directory
