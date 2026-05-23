## ADDED Requirements

### Requirement: Config-driven provider registry
The system SHALL maintain a registry of named LLM providers, each resolved to an `IChatClient` instance from configuration. Providers are defined in `.daemon/config.yaml` (project) or `~/.daemon/config.yaml` (user-global).

#### Scenario: Provider resolved at startup
- **WHEN** the agent core starts
- **THEN** all providers defined in config are validated and their `IChatClient` factories are registered

#### Scenario: Unknown provider in config
- **WHEN** a config entry specifies an unsupported adapter type
- **THEN** the core logs a warning and skips that provider; startup continues

### Requirement: Supported provider adapters
The system SHALL support the following adapter types, each backed by an `IChatClient` implementation:

- `openai` — OpenAI .NET SDK; supports custom `baseUrl` for Ollama, llama.cpp, oMLX
- `anthropic` — `Anthropic.SDK` community package
- `gemini` — `GeminiDotnet.Extensions.AI`

#### Scenario: OpenAI-compatible local provider
- **WHEN** a provider is configured with adapter `openai` and a `baseUrl` of `http://localhost:11434/v1`
- **THEN** the registry creates an `IChatClient` pointing at that endpoint

#### Scenario: oMLX via Anthropic adapter
- **WHEN** a provider is configured with adapter `anthropic` and a `baseUrl` pointing to an oMLX instance
- **THEN** the registry creates an Anthropic `IChatClient` targeting the oMLX endpoint

### Requirement: Runtime provider switching
The agent core SHALL switch the active `IChatClient` at runtime without restarting the process.

#### Scenario: Switch provider by command
- **WHEN** the host sends `model.set {provider, modelId}` outside a turn
- **THEN** the agent loop uses the new provider for the next turn and emits `providerSwitched {name, model, effectiveNextTurn: true}`

#### Scenario: Mid-turn switch defers to next turn
- **WHEN** the host sends `model.set` while a turn is in flight
- **THEN** the current LLM call completes on the previous provider, the new provider takes effect on the next turn, and `providerSwitched {..., effectiveNextTurn: true}` is emitted

#### Scenario: Cycle through providers
- **WHEN** the host sends `model.cycle`
- **THEN** the active provider advances to the next in the configured list

### Requirement: Model capability metadata
Each provider entry SHALL declare a `Model` object with capability fields used by the agent loop for capability negotiation.

#### Scenario: Capability fields present
- **WHEN** the host sends `model.list`
- **THEN** each model entry includes `toolCalling`, `reasoning`, `input`, `contextWindow`, and `maxTokens`

#### Scenario: Agent adapts to incapable model
- **WHEN** the active model has `toolCalling: false`
- **THEN** the agent loop does not include tools in `ChatOptions` and does not attempt tool dispatch

#### Scenario: Context window respected on compaction trigger
- **WHEN** estimated token usage exceeds the configured fraction of `contextWindow`
- **THEN** compaction is triggered (see agent-core spec)

#### Scenario: maxTokens caps response size
- **WHEN** the agent loop builds `ChatOptions` for a turn
- **THEN** `ChatOptions.MaxOutputTokens` is set to the model's declared `maxTokens` unless overridden by config
