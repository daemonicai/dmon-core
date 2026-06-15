## MODIFIED Requirements

### Requirement: System prompt assembly

The agent core SHALL assemble a system prompt at the start of each session by combining a resolved base string with dynamic context. The assembled prompt SHALL be injected as a `ChatRole.System` message at index 0 of the conversation history before any user turn is processed. The core SHALL inject **no hidden scaffolding** into the resolved string beyond the dynamic-context and project-config sections defined by this capability — tools reach the model through M.E.AI `ChatOptions`, not the prompt, so what the composition root sets is what the model sees.

#### Scenario: System message prepended on first turn
- **WHEN** the host sends the first `turn.submit` of a session
- **THEN** the core prepends the assembled system message to history before calling the LLM pipeline

#### Scenario: System message not rebuilt on subsequent turns
- **WHEN** the host sends a second or later `turn.submit` in the same session
- **THEN** the system message at index 0 is unchanged and no rebuild occurs

#### Scenario: No tool scaffolding injected into the prompt
- **WHEN** a session with loaded tool extensions starts
- **THEN** the assembled system message contains no tool-description or tool-invocation scaffolding, and the tools are advertised to the model only via `ChatOptions`

### Requirement: System prompt base content

The system prompt base SHALL be a plain string with no "persona" concept. It SHALL resolve as: the value of `UseSystemPrompt(string)` if called in the composition root; otherwise the `systemPrompt` value on `IConfiguration` (supplied via `config.yaml`, an environment variable, or `args`); otherwise the core's built-in default. `UseSystemPrompt` SHALL **replace** the base in full. `AppendToSystemPrompt(string)` SHALL **extend** the resolved base by ordered append, and multiple appends SHALL compose in call order, so `final = base + ordered appends`. Precedence between sources SHALL be `config systemPrompt` < `UseSystemPrompt`; appends apply on top of whichever base wins.

The core's built-in default SHALL:
- Identify the agent as "D-mon" (a coding agent)
- State tool-usage norms: read before editing, prefer targeted edits over full rewrites, ask one short question if scope is genuinely unclear
- State permission model awareness: bash commands and file writes require user confirmation; the runtime handles this
- Use an informal, terse tone with no padding, hedging, or apologies

#### Scenario: Built-in default present when nothing is set
- **WHEN** a session starts with no `UseSystemPrompt` call and no `systemPrompt` configuration value
- **THEN** the system message base is the core's built-in default D-mon coding content

#### Scenario: Config systemPrompt overrides the built-in default
- **WHEN** `IConfiguration["systemPrompt"]` is set and the composition root does not call `UseSystemPrompt`
- **THEN** the system message base is the config value, not the built-in default

#### Scenario: UseSystemPrompt replaces the base and outranks config
- **WHEN** the composition root calls `UseSystemPrompt("You are a tiny beetle…")` and `IConfiguration["systemPrompt"]` is also set
- **THEN** the system message base is the `UseSystemPrompt` value in full, ignoring the config value

#### Scenario: AppendToSystemPrompt composes in order on top of the base
- **WHEN** the composition root resolves a base and then calls `AppendToSystemPrompt("A")` followed by `AppendToSystemPrompt("B")`
- **THEN** the resolved string is `base + "A" + "B"` in that order

### Requirement: Dynamic context assembly

The system prompt SHALL include a dynamic context block assembled at session start containing: working directory (absolute path), OS and platform, active provider name and model ID, and the list of currently loaded extensions (if any). When the composition root has enabled session assets (the `UseAssets` verb; see the `agent-profiles` removal in this change), the dynamic context block SHALL additionally state the per-session asset directory (`assets/<session_id>/`); when assets are not enabled, the asset directory SHALL NOT be mentioned.

#### Scenario: Working directory included
- **WHEN** the session starts
- **THEN** the system message includes the absolute path of the process working directory

#### Scenario: Extension list included when extensions are loaded
- **WHEN** one or more extensions are loaded at session start
- **THEN** the system message lists their names in the dynamic context block

#### Scenario: Extension list omitted when none loaded
- **WHEN** no extensions are loaded
- **THEN** the dynamic context block omits the extensions section

#### Scenario: Asset directory surfaced only when assets are enabled
- **WHEN** the composition root has enabled session assets via `UseAssets`
- **THEN** the dynamic context block states the session's `assets/<session_id>/` directory

#### Scenario: Asset directory omitted when assets are not enabled
- **WHEN** the composition root has not enabled session assets
- **THEN** the dynamic context block makes no mention of an asset directory

## REMOVED Requirements

### Requirement: Static core content

**Reason**: ADR-022 Decision 11 removes the "persona" concept entirely. The system prompt is a plain string, not a persona block supplied by an active agent profile; with profiles dissolved (ADR-022 Decision 14, ADR-013 superseded) there is no profile to supply or replace a persona block. The base-content guarantees are re-expressed without persona framing by the new "System prompt base content" requirement above.

**Migration**: The former coding-profile persona becomes the core's built-in default base string (the lowest-precedence source). "Replacing the persona" becomes `UseSystemPrompt(string)` (replace) or `IConfiguration["systemPrompt"]` (config override); "augmenting" becomes `AppendToSystemPrompt(string)`. Full control beyond replace/append remains via the `Services.AddSingleton<ISystemPromptBuilder>(…)` raw-DI escape hatch.

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
