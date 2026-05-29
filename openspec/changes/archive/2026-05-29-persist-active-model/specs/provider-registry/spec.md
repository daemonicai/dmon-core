## ADDED Requirements

### Requirement: Active provider and model selection is persisted

The system SHALL persist the active selection as a single `{provider}/{model}` model reference so it survives a restart. The provider key is the substring before the first `/`; the model id is everything after the first `/`, taken verbatim (it may itself contain `/`). Persistence uses a git-ignored, app-managed `config.local.yaml` at **project scope** (`./.dmon/config.local.yaml`), participating as the highest-precedence `IConfiguration` layer, with the selection under the top-level `activeModel` key.

The active selection SHALL be read through `IConfiguration` (i.e. `activeModel` resolved across the config-layer stack) and parsed into a model reference. When a provider/model switch is committed, the new `{provider}/{model}` SHALL be written to `./.dmon/config.local.yaml` atomically (temp file then move), preserving any other top-level keys present.

At startup `ProviderRegistry` SHALL initialise its active provider index and active model id from the parsed `activeModel` when the referenced provider is currently configured. When `activeModel` is absent, unparseable, or names a provider that is not configured, the registry SHALL fall back to the default (first configured provider, index 0) without throwing.

#### Scenario: Selection saved when a switch is committed

- **WHEN** a pending provider/model switch is committed
- **THEN** `./.dmon/config.local.yaml` is written with `activeModel: {provider}/{model}` for the newly active selection

#### Scenario: Selection restored at startup

- **WHEN** the agent core starts and `IConfiguration` resolves `activeModel` to a `{provider}/{model}` whose provider is configured
- **THEN** `ProviderRegistry` makes that provider active and sets `GetCurrentModelId()` to the referenced model id, instead of defaulting to the first configured provider

#### Scenario: Model reference splits on the first slash

- **WHEN** `activeModel` is `ollama/deepseek/deepseek-v4-pro`
- **THEN** it resolves to provider `ollama` and model id `deepseek/deepseek-v4-pro` (everything after the first slash is the provider-owned model id, passed through unmolested)

#### Scenario: Absent or stale selection falls back to default

- **WHEN** no `activeModel` is configured, or it is unparseable, or the referenced provider is no longer configured
- **THEN** the registry uses the default first configured provider (index 0) and does not throw

## MODIFIED Requirements

### Requirement: Config-driven provider registry
The system SHALL maintain a registry of named LLM providers, each resolved to an `IChatClient` instance from configuration. Providers are defined in `~/.dmon/config.yaml` (user-global) and `./.dmon/config.yaml` (project). Configuration is layered via `IConfiguration` with precedence, lowest to highest: `~/.dmon/config.yaml` < `./.dmon/config.yaml` < `./.dmon/config.local.yaml` (the git-ignored, app-managed local layer). A later layer overrides the same key in an earlier layer.

#### Scenario: Provider resolved at startup
- **WHEN** the agent core starts
- **THEN** all providers defined in config are validated and their `IChatClient` factories are registered

#### Scenario: Unknown adapter at startup
- **WHEN** a config entry specifies an adapter type with no registered `IProviderFactory`
- **THEN** `ProviderRegistry` throws `InvalidOperationException` at construction with a message naming the unknown adapter

#### Scenario: Project config overrides global
- **WHEN** the same configuration key is set in both `~/.dmon/config.yaml` and `./.dmon/config.yaml`
- **THEN** the project value takes precedence over the global value

#### Scenario: Local layer overrides project and global
- **WHEN** a key (e.g. `activeModel`) is set in `./.dmon/config.local.yaml`
- **THEN** it overrides the same key in `./.dmon/config.yaml` and `~/.dmon/config.yaml`

### Requirement: ProviderRegistry tracks and commits pending provider and model switches
`ProviderRegistry` SHALL maintain a pending provider index and pending model ID, each independently nullable (set by `SetProvider` and `SetModel` respectively). `CommitPendingSwitch` SHALL apply both pending values atomically, dispose the previous `IChatClient`, and return a `ProviderSwitchResult?` (null if nothing was pending). `TurnHandler` is responsible for mapping `ProviderSwitchResult` to `ProviderSwitchedEvent` before emitting.

#### Scenario: Provider switch committed between turns
- **WHEN** `SetProvider("openai")` is called during a turn and `CommitPendingSwitch` is called after the turn ends
- **THEN** the previous client is disposed, a new client is created via `OpenAiProviderFactory`, and a non-null `ProviderSwitchResult` is returned

#### Scenario: Model switch committed independently
- **WHEN** `SetModel("claude-haiku-4-5-20251001")` is called and `CommitPendingSwitch` is called
- **THEN** the provider stays the same, the client is recreated with the new model ID, and a `ProviderSwitchResult` is returned

#### Scenario: No pending change returns null
- **WHEN** `CommitPendingSwitch` is called with no pending provider or model change
- **THEN** it returns null and the active client is not disposed

#### Scenario: Mid-turn switch defers to next turn
- **WHEN** the host sends `model.set` while a turn is in flight
- **THEN** the current LLM call completes on the previous provider, the queued switch is committed at the start of the next turn before the provider client is resolved, and a single `providerSwitched {..., effectiveNextTurn: false}` is emitted at that point (no `providerSwitched` is emitted while the in-flight turn is still running)

#### Scenario: Cycle through providers
- **WHEN** the host sends `model.cycle`
- **THEN** the active provider advances to the next in the configured list
