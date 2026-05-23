## REMOVED Requirements

### Requirement: ProviderRegistry constructs IChatClient directly from SDK types
**Reason**: Client construction now belongs to `IProviderFactory` implementations in `Daemon.Providers`. Hard-coding SDK construction in `ProviderRegistry` prevents adding new providers without modifying `Daemon.Core`.
**Migration**: Replace calls to internal `CreateClientAsync` with `_factories[config.Adapter].CreateAsync(config, apiKey, cancellationToken)`. Existing behaviour is preserved by the three built-in factory implementations.

### Requirement: IPermissionPolicy capability booleans in provider config
**Reason**: Static `toolCalling` / `reasoning` booleans in `appsettings.json` are replaced by per-model-id defaults in `IProviderFactory.GetCapabilities`. Config-level booleans are removed to avoid the dual-source-of-truth problem.
**Migration**: Remove `capabilities.toolCalling` and `capabilities.reasoning` from provider entries in `appsettings.json`. Capabilities are now derived from the factory.

### Requirement: SetProvider accepts an optional modelId parameter
**Reason**: Provider switching and model switching are independent concerns. The combined `SetProvider(name, modelId?)` conflates them. `SetModel` is now a separate operation.
**Migration**: Replace `SetProvider("anthropic", "claude-haiku-4-5-20251001")` with `SetProvider("anthropic")` followed by `SetModel("claude-haiku-4-5-20251001")`.

## MODIFIED Requirements

### Requirement: ProviderRegistry tracks and commits pending provider switch
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

#### Scenario: CommitPendingSwitch returns ProviderSwitchResult not ProviderSwitchedEvent
- **WHEN** `CommitPendingSwitch` returns a non-null value
- **THEN** the return type is `ProviderSwitchResult` (a plain Core record); the caller maps it to `ProviderSwitchedEvent`

### Requirement: CurrentSupportsToolCalling and CurrentSupportsReasoning reflect factory capabilities
`IProviderRegistry.CurrentSupportsToolCalling` and `CurrentSupportsReasoning` SHALL return values derived from `ChatClientCapabilities`. If a client is active, the value is read from `client.GetService(typeof(ChatClientCapabilities))`. If no client is active yet, the value is read from `IProviderFactory.GetCapabilities(config.DefaultModelId)`.

#### Scenario: Active client capabilities take precedence
- **WHEN** a client is active and `GetService(typeof(ChatClientCapabilities))` returns a non-null result
- **THEN** `CurrentSupportsToolCalling` returns the value from that result

#### Scenario: No active client falls back to factory defaults
- **WHEN** no client has been created yet for the current provider
- **THEN** `CurrentSupportsToolCalling` returns the value from `factory.GetCapabilities(config.DefaultModelId ?? "")`

## ADDED Requirements

### Requirement: SetModel queues an independent model switch
`IProviderRegistry` SHALL expose `void SetModel(string modelId)`. Calling `SetModel` SHALL queue the model ID for application at the next `CommitPendingSwitch` call. If the model ID is not found in the factory's known model list, a warning SHALL be logged but the switch SHALL proceed (conservative: unknown models use safe capability defaults).

#### Scenario: SetModel queues without immediate effect
- **WHEN** `SetModel("claude-haiku-4-5-20251001")` is called during a turn
- **THEN** the active client is unchanged until `CommitPendingSwitch` is called

#### Scenario: Unknown model ID proceeds with warning
- **WHEN** `SetModel("some-future-model")` is called and the factory does not recognise the model ID
- **THEN** a warning is logged, the switch is queued, and the model resolves to conservative capabilities (`SupportsToolCalling = false`)
