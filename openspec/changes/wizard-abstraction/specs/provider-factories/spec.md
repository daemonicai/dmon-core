## MODIFIED Requirements

### Requirement: IProviderFactory interface for pluggable client construction
The system SHALL define `IProviderFactory` in `Dmon.Abstractions` with the following members: `string AdapterName`, `string DisplayName`, `string DefaultEnvVar`, `string DefaultModelId`, `ChatClientCapabilities GetCapabilities(string modelId)`, `ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken)`, `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)`, and `ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)`. `GetAvailableModelsAsync` SHALL retain a default interface implementation returning an empty list. `GetNextStepAsync` and `DisplayName` SHALL NOT have default implementations, so every factory is required to define its setup flow and human-readable name. `ProviderRegistry` SHALL resolve the correct factory by matching `ProviderConfig.Adapter` against `IProviderFactory.AdapterName` (case-insensitive). `DisplayName` is the human-readable label shown in the setup wizard; `AdapterName` remains the machine-readable identifier stored in config.

#### Scenario: Known adapter resolves to correct factory
- **WHEN** a `ProviderConfig` with `Adapter = "anthropic"` is active
- **THEN** `ProviderRegistry` uses the `AnthropicProviderFactory` to create the client

#### Scenario: Unknown adapter throws at startup
- **WHEN** a `ProviderConfig` with an `Adapter` value that matches no registered factory is present
- **THEN** `ProviderRegistry` throws `InvalidOperationException` during initialisation with a message naming the unknown adapter

#### Scenario: External implementor compiles without adding GetAvailableModelsAsync
- **WHEN** an external type implements `IProviderFactory` without defining `GetAvailableModelsAsync`
- **THEN** the project compiles successfully because the default interface implementation is used

#### Scenario: Factory exposes a display name distinct from the adapter name
- **WHEN** the setup wizard enumerates registered factories
- **THEN** each factory provides a `DisplayName` (e.g. `"Anthropic"`) for presentation and an `AdapterName` (e.g. `"anthropic"`) for the stored config value

## ADDED Requirements

### Requirement: Built-in factories implement the wizard setup flow
`OpenAiProviderFactory`, `AnthropicProviderFactory`, and `GeminiProviderFactory` SHALL each implement `GetNextStepAsync` to drive their setup as a state machine: when no API key answer is present in `WizardState`, return a `TextInputStep` (id `"api-key"`, `Secret = true`, `Required = true`); when an API key is present but no model is chosen, return a `ChooseOneStep` (id `"model"`) populated from `GetAvailableModelsAsync`; when both are present, populate the in-flight `ProviderConfig` and return a `WizardCompletedStep` carrying a confirmation message.

#### Scenario: First step requests the API key
- **WHEN** `GetNextStepAsync` is called with a `WizardState` containing only the adapter-selection step
- **THEN** the returned step is a `TextInputStep` with id `"api-key"`, `Secret = true`, and `Required = true`

#### Scenario: Second step lists models
- **WHEN** `GetNextStepAsync` is called with a `WizardState` that contains an answered `"api-key"` step but no answered `"model"` step
- **THEN** the returned step is a `ChooseOneStep` with id `"model"` whose options come from `GetAvailableModelsAsync`

#### Scenario: Flow completes once model is chosen
- **WHEN** `GetNextStepAsync` is called with a `WizardState` containing answered `"api-key"` and `"model"` steps
- **THEN** the factory populates the in-flight `ProviderConfig` and returns a `WizardCompletedStep` with a non-empty `Message`
