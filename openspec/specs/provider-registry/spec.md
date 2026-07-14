## Purpose

Define how dmon discovers, validates, and switches between named LLM providers — each resolved from configuration to an `IChatClient` — so the agent core and hosts can list providers, select a model, and fail loudly on a misconfigured provider set.
## Requirements
### Requirement: Config-driven provider registry
The system SHALL maintain a registry of named LLM providers, each resolved to an `IChatClient` instance. The provider list SHALL be composed from two sources: (1) **default `ProviderConfig` instances synthesized from the `IProviderFactory` set wired in the composition root** (see the `provider-factories` capability), and (2) **optional** entries in `~/.dmon/config.yaml` (user-global) and `./.dmon/config.yaml` (project). A `config.yaml` `providers:` entry overrides or augments a synthesized default keyed by adapter/name; the `providers:` map is OPTIONAL and a wired provider SHALL be usable with no `config.yaml` entry. Configuration is layered via `IConfiguration` with precedence, lowest to highest: `~/.dmon/config.yaml` < `./.dmon/config.yaml` < `./.dmon/config.local.yaml` (the git-ignored, app-managed local layer). A later layer overrides the same key in an earlier layer. The composed provider list SHALL be visible to every consumer of the shared `IEnumerable<ProviderConfig>` (notably both `ProviderRegistry` and `CredentialResolver`), so a synthesized provider's credentials resolve identically to a config-declared one.

#### Scenario: Provider resolved at startup
- **WHEN** the agent core starts
- **THEN** all providers — synthesized from wired factories and any declared in config — are present in the registry and their `IChatClient` factories are registered

#### Scenario: Unknown adapter at startup
- **WHEN** a `config.yaml` entry specifies an adapter type with no registered `IProviderFactory`
- **THEN** `ProviderRegistry` logs a warning naming the provider and adapter and skips that entry, and startup proceeds (it does NOT throw)

#### Scenario: Project config overrides global
- **WHEN** the same configuration key is set in both `~/.dmon/config.yaml` and `./.dmon/config.yaml`
- **THEN** the project value takes precedence over the global value

#### Scenario: Local layer overrides project and global
- **WHEN** a key (e.g. `activeModel`) is set in `./.dmon/config.local.yaml`
- **THEN** it overrides the same key in `./.dmon/config.yaml` and `~/.dmon/config.yaml`

### Requirement: Supported provider adapters
The system SHALL support the following adapter types via `IProviderFactory` implementations in `Dmon.Providers`:

- `openai` — `Microsoft.Extensions.AI.OpenAI`; supports custom `baseUrl` for Ollama, llama.cpp, and other OpenAI-compatible local runtimes
- `anthropic` — `Anthropic.SDK` community package
- `gemini` — `GeminiDotnet.Extensions.AI`

#### Scenario: OpenAI-compatible local provider
- **WHEN** a provider is configured with adapter `openai` and a `baseUrl` of `http://localhost:11434/v1`
- **THEN** the registry creates an `IChatClient` pointing at that endpoint

#### Scenario: Anthropic-compatible local endpoint via baseUrl
- **WHEN** a provider is configured with adapter `anthropic` and a `baseUrl` pointing to a local Anthropic-compatible endpoint
- **THEN** the registry creates an Anthropic `IChatClient` targeting that endpoint

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

### Requirement: Provider config schema is validated at load

The `providers` block SHALL be a map keyed by provider name. `ProviderConfigLoader` SHALL validate this shape at load time and throw `InvalidOperationException` with an actionable message when the block is malformed, rather than producing providers with index-derived or empty names. The error message SHALL name the offending entry and state the canonical map-keyed schema (`adapter`, optional `defaultModelId`/`baseUrl`, and `auth.type`/`auth.envVar`).

A provider name derived from the configuration section key SHALL be rejected when it is purely numeric (the shape produced when `providers` is written as a YAML sequence) or empty/whitespace.

#### Scenario: Providers written as a YAML sequence

- **WHEN** the `providers` block is authored as a YAML sequence (`- provider: anthropic …`), so the configuration binder keys the entries `0`, `1`, `2`, …
- **THEN** `ProviderConfigLoader.Load` throws `InvalidOperationException` whose message names the numeric key and states that `providers` must be a map keyed by provider name

#### Scenario: Empty or whitespace provider name

- **WHEN** a provider entry's key is empty or whitespace
- **THEN** `ProviderConfigLoader.Load` throws `InvalidOperationException` identifying the offending entry

#### Scenario: Missing adapter

- **WHEN** a map-keyed provider entry omits the required `adapter` field
- **THEN** `ProviderConfigLoader.Load` throws `InvalidOperationException` naming the provider and the missing `adapter` field

#### Scenario: Valid map-keyed providers load

- **WHEN** the `providers` block is a map keyed by name, each entry carrying `adapter` and optionally `defaultModelId`, `baseUrl`, and an `auth` block
- **THEN** `ProviderConfigLoader.Load` returns one `ProviderConfig` per entry whose `Name` is the map key, with `auth.type` defaulting to `none` when the `auth` block is omitted

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

### Requirement: Provider extensions are populated by build-time DI-discovery
`IProviderRegistry` SHALL be populated with provider extensions at **build time** by enumerating `IEnumerable<IProviderExtension>` from the DI container and routing each through the existing `IProviderRegistry.RegisterExtensionAsync`, gated by `IsApplicable()`. There SHALL be no manual post-build registration loop and no dynamic-loader path: the registry's provider-extension contents are exactly the applicable `IProviderExtension` instances registered in DI (via `AddProvider<T>()` / `Use<Provider>` verbs). This population SHALL NOT alter the registry's runtime/session-state surface (`GetCurrentAsync`, `SetProvider`, `SetModel`); it only changes how the registry comes to hold its providers.

#### Scenario: Registry enumerates provider extensions from DI at build time
- **WHEN** the host is built and the container holds registered `IProviderExtension` instances
- **THEN** the registry enumerates `IEnumerable<IProviderExtension>` and routes each applicable one via `RegisterExtensionAsync`, with no separate manual or dynamic-loader registration step

#### Scenario: Inapplicable provider extension is not registered
- **WHEN** a registered `IProviderExtension` returns `false` from `IsApplicable()` during build-time enumeration
- **THEN** it is skipped and does not appear in the registry

#### Scenario: Runtime selection surface is unchanged
- **WHEN** providers have been populated by build-time DI-discovery
- **THEN** `GetCurrentAsync`, `SetProvider`, and `SetModel` behave exactly as before; only the population path has changed

### Requirement: Provider list synthesized from wired factories with config override
The system SHALL synthesize a default `ProviderConfig` for each wired cloud `IProviderFactory` whose `AdapterName` is not already the `Adapter` of any `config.yaml`-derived entry (case-insensitive), and SHALL merge these defaults with the config-derived entries into the single shared provider list. Config-derived entries SHALL appear first (preserving existing index-0/default-selection behavior); synthesized defaults SHALL follow in factory registration order (i.e. `Use<Provider>()` order in `Dmon.cs`). When the `providers:` map is absent or empty, the default provider (index 0, absent a restored selection) SHALL be the first wired provider.

#### Scenario: Verb-only provider is usable without a config entry
- **WHEN** `Dmon.cs` wires `UseAnthropic()` and `.dmon/config.yaml` has no `providers:` entry for it
- **THEN** the registry lists an `anthropic` provider whose `DefaultModelId` and credential env var come from `AnthropicProviderFactory`, and a turn can run against it once its API key is present

#### Scenario: Config entry overrides the synthesized default
- **WHEN** a factory for adapter `openai` is wired AND `config.yaml` declares a provider with `adapter: openai` and a custom `baseUrl`/`defaultModelId`
- **THEN** the config-declared entry is used for that adapter and no duplicate default is synthesized for it

#### Scenario: No providers map and no wired factories is fatal
- **WHEN** `config.yaml` has no `providers:` map AND no `Use<Provider>()` verb has been called
- **THEN** the registry constructs without throwing, but resolving the current provider (`GetCurrentConfig`/`GetCurrentAsync`) throws `InvalidOperationException` ("At least one provider must be configured.")

#### Scenario: No providers map but a factory is wired is not fatal
- **WHEN** `config.yaml` has no `providers:` map AND at least one `Use<Provider>()` verb has been called
- **THEN** the registry is constructed successfully with the synthesized provider(s) and resolving the current provider does not throw

