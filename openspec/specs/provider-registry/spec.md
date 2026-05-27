## Requirements

### Requirement: Config-driven provider registry
The system SHALL maintain a registry of named LLM providers, each resolved to an `IChatClient` instance from configuration. Providers are defined in `.daemon/config.yaml` (project) or `~/.daemon/config.yaml` (user-global).

#### Scenario: Provider resolved at startup
- **WHEN** the agent core starts
- **THEN** all providers defined in config are validated and their `IChatClient` factories are registered

#### Scenario: Unknown adapter at startup
- **WHEN** a config entry specifies an adapter type with no registered `IProviderFactory`
- **THEN** `ProviderRegistry` throws `InvalidOperationException` at construction with a message naming the unknown adapter

### Requirement: Supported provider adapters
The system SHALL support the following adapter types via `IProviderFactory` implementations in `Dmon.Providers`:

- `openai` — `Microsoft.Extensions.AI.OpenAI`; supports custom `baseUrl` for Ollama, llama.cpp, oMLX
- `anthropic` — `Anthropic.SDK` community package
- `gemini` — `GeminiDotnet.Extensions.AI`

#### Scenario: OpenAI-compatible local provider
- **WHEN** a provider is configured with adapter `openai` and a `baseUrl` of `http://localhost:11434/v1`
- **THEN** the registry creates an `IChatClient` pointing at that endpoint

#### Scenario: oMLX via Anthropic adapter
- **WHEN** a provider is configured with adapter `anthropic` and a `baseUrl` pointing to an oMLX instance
- **THEN** the registry creates an Anthropic `IChatClient` targeting the oMLX endpoint

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
- **THEN** the current LLM call completes on the previous provider, the new provider takes effect on the next turn, and `providerSwitched {..., effectiveNextTurn: true}` is emitted

#### Scenario: Cycle through providers
- **WHEN** the host sends `model.cycle`
- **THEN** the active provider advances to the next in the configured list

### Requirement: SetModel queues an independent model switch
`IProviderRegistry` SHALL expose `void SetModel(string modelId)`. Calling `SetModel` SHALL queue the model ID for application at the next `CommitPendingSwitch` call. If the model ID is not found in the factory's known model list, a warning SHALL be logged but the switch SHALL proceed (conservative: unknown models use safe capability defaults).

#### Scenario: SetModel queues without immediate effect
- **WHEN** `SetModel("claude-haiku-4-5-20251001")` is called during a turn
- **THEN** the active client is unchanged until `CommitPendingSwitch` is called

#### Scenario: Unknown model ID proceeds with warning
- **WHEN** `SetModel("some-future-model")` is called and the factory does not recognise the model ID
- **THEN** a warning is logged, the switch is queued, and the model resolves to conservative capabilities (`SupportsToolCalling = false`)

### Requirement: CurrentSupportsToolCalling and CurrentSupportsReasoning reflect factory capabilities
`IProviderRegistry.CurrentSupportsToolCalling` and `CurrentSupportsReasoning` SHALL return values derived from `ChatClientCapabilities`. If a client is active, the value is read from `client.GetService(typeof(ChatClientCapabilities))`. If no client is active yet, the value is read from `IProviderFactory.GetCapabilities(config.DefaultModelId)`.

#### Scenario: Active client capabilities take precedence
- **WHEN** a client is active and `GetService(typeof(ChatClientCapabilities))` returns a non-null result
- **THEN** `CurrentSupportsToolCalling` returns the value from that result

#### Scenario: No active client falls back to factory defaults
- **WHEN** no client has been created yet for the current provider
- **THEN** `CurrentSupportsToolCalling` returns the value from `factory.GetCapabilities(config.DefaultModelId ?? "")`

#### Scenario: Agent adapts to incapable model
- **WHEN** the active model has `CurrentSupportsToolCalling = false`
- **THEN** the agent loop does not include tools in `ChatOptions` and does not attempt tool dispatch

### Requirement: Model capability metadata exposed via model.list
Each provider entry SHALL expose a `Model` object with capability fields sourced from `IProviderFactory.GetCapabilities`. Config-level `toolCalling` and `reasoning` booleans are not used; capabilities are factory-owned.

#### Scenario: Capability fields present
- **WHEN** the host sends `model.list`
- **THEN** each model entry includes `toolCalling`, `reasoning`, `input`, `contextWindow`, and `maxTokens` sourced from the factory's `GetCapabilities(defaultModelId)`

### Requirement: ProviderRegistry tracks committed active model ID
`ProviderRegistry` SHALL maintain a `_activeModelId` field that is updated atomically inside `CommitPendingSwitch()` whenever a pending model ID is applied. `IProviderRegistry` SHALL expose this value via `string? GetCurrentModelId()`.

#### Scenario: GetCurrentModelId returns null before first commit
- **WHEN** `GetCurrentModelId()` is called before any `CommitPendingSwitch()` has applied a model change
- **THEN** it returns null

#### Scenario: GetCurrentModelId reflects committed model
- **WHEN** `SetModel("claude-haiku-4-5-20251001")` is called and `CommitPendingSwitch()` is called
- **THEN** `GetCurrentModelId()` returns `"claude-haiku-4-5-20251001"`

#### Scenario: GetCurrentModelId survives subsequent provider-only switch
- **WHEN** `SetProvider("openai")` is called (no `SetModel`) and `CommitPendingSwitch()` is called
- **THEN** `GetCurrentModelId()` retains the previously committed model ID (the provider switch alone does not clear it)

#### Scenario: GetCurrentModelId updates after model switch on different provider
- **WHEN** `SetProvider("openai")` and `SetModel("gpt-4o")` are both called before `CommitPendingSwitch()`
- **THEN** after commit, `GetCurrentModelId()` returns `"gpt-4o"`
