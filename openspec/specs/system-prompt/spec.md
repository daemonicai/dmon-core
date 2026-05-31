## Purpose

Define how the agent core assembles the system prompt at the start of each session by combining a static core identity with dynamic runtime context and discovered project configuration files, and how the assembled message is injected as the first entry in the conversation history.
## Requirements
### Requirement: System prompt assembly
The agent core SHALL assemble a system prompt at the start of each session by combining a static core with dynamic context. The assembled prompt SHALL be injected as a `ChatRole.System` message at index 0 of the conversation history before any user turn is processed.

#### Scenario: System message prepended on first turn
- **WHEN** the host sends the first `turn.submit` of a session
- **THEN** the core prepends the assembled system message to history before calling the LLM pipeline

#### Scenario: System message not rebuilt on subsequent turns
- **WHEN** the host sends a second or later `turn.submit` in the same session
- **THEN** the system message at index 0 is unchanged and no rebuild occurs

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

### Requirement: Project config discovery
The agent core SHALL discover and include project config content in the system prompt by resolving config files in this order:

1. `~/.dmon/AGENTS.md` — user-level config, read silently
2. `{CWD}/AGENTS.md` — project config, read silently
3. `{CWD}/CLAUDE.md` — compatibility fallback, only if no `{CWD}/AGENTS.md` exists

When both user-level and project-level configs are present, both SHALL be included with the user config appearing first. The content SHALL be included verbatim as free-form markdown.

#### Scenario: Project AGENTS.md read silently
- **WHEN** `{CWD}/AGENTS.md` exists
- **THEN** its content is included in the system message without any notice event emitted

#### Scenario: User AGENTS.md combined with project AGENTS.md
- **WHEN** both `~/.dmon/AGENTS.md` and `{CWD}/AGENTS.md` exist
- **THEN** both are included, user config first, separated by a section header

#### Scenario: CLAUDE.md compat offer
- **WHEN** `{CWD}/CLAUDE.md` exists and `{CWD}/AGENTS.md` does not exist
- **THEN** the core emits `system.notice {message: "Found CLAUDE.md — using it as project config. Rename to AGENTS.md to suppress this notice."}` and includes the file content in the system message

#### Scenario: No config files found
- **WHEN** neither `~/.dmon/AGENTS.md`, `{CWD}/AGENTS.md`, nor `{CWD}/CLAUDE.md` exists
- **THEN** the system message is assembled without any project config section and no notice is emitted

