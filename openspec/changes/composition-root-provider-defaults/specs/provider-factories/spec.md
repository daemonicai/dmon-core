## MODIFIED Requirements

### Requirement: IProviderFactory interface for pluggable client construction
The system SHALL define `IProviderFactory` in `Dmon.Abstractions` with the following members: `string AdapterName`, `string DisplayName`, `string DefaultEnvVar`, `string DefaultModelId`, `ChatClientCapabilities GetCapabilities(string modelId)`, `ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken)`, `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)`, and `ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)`. `GetAvailableModelsAsync` SHALL retain a default interface implementation returning an empty list. `GetNextStepAsync` and `DisplayName` SHALL NOT have default implementations, so every factory is required to define its setup flow and human-readable name. `ProviderRegistry` SHALL resolve the correct factory by matching `ProviderConfig.Adapter` against `IProviderFactory.AdapterName` (case-insensitive). `DisplayName` is the human-readable label shown in the setup wizard; `AdapterName` remains the machine-readable identifier stored in config.

#### Scenario: Known adapter resolves to correct factory
- **WHEN** a `ProviderConfig` with `Adapter = "anthropic"` is active
- **THEN** `ProviderRegistry` uses the `AnthropicProviderFactory` to create the client

#### Scenario: Unknown adapter is skipped with a warning
- **WHEN** a `config.yaml`-derived `ProviderConfig` has an `Adapter` value that matches no registered factory
- **THEN** that entry is skipped with a logged warning naming the unknown adapter and startup proceeds (it does NOT throw)

#### Scenario: External implementor compiles without adding GetAvailableModelsAsync
- **WHEN** an external type implements `IProviderFactory` without defining `GetAvailableModelsAsync`
- **THEN** the project compiles successfully because the default interface implementation is used

#### Scenario: Factory exposes a display name distinct from the adapter name
- **WHEN** the setup wizard enumerates registered factories
- **THEN** each factory provides a `DisplayName` (e.g. `"Anthropic"`) for presentation and an `AdapterName` (e.g. `"anthropic"`) for the stored config value

## ADDED Requirements

### Requirement: A cloud Use verb contributes a default ProviderConfig
A cloud `Use<Provider>()` verb SHALL make its provider usable from the composition root alone: the wired `IProviderFactory` SHALL yield a default `ProviderConfig` (synthesized where the shared provider list is composed) so that no `config.yaml` entry is required. The synthesized default SHALL set `Name` and `Adapter` to `IProviderFactory.AdapterName`, `DefaultModelId` to `IProviderFactory.DefaultModelId`, and `Auth` to `{ Type = "envVar", EnvVar = DefaultEnvVar }` when `DefaultEnvVar` is non-empty (otherwise `{ Type = "none" }`), with `BaseUrl` null. A default SHALL be synthesized only when no config-derived entry already represents that adapter. This brings cloud `IProviderFactory` verbs to parity with local `IProviderExtension` providers, which already synthesize a `ProviderConfig` at registration.

#### Scenario: Cloud verb makes its provider usable with no config
- **WHEN** `Dmon.cs` calls `UseAnthropic()` and `config.yaml` declares no `anthropic` provider
- **THEN** a default `ProviderConfig` (`Name`/`Adapter` = `"anthropic"`, model and env var from the factory) is present in the shared provider list and the provider is selectable

#### Scenario: Synthesized default suppressed when config represents the adapter
- **WHEN** a factory for adapter `openai` is wired AND `config.yaml` already declares a provider with `adapter: openai`
- **THEN** no default is synthesized for `openai` and the config-declared entry is used

#### Scenario: Factory without an env var synthesizes a keyless default
- **WHEN** a wired factory reports an empty `DefaultEnvVar`
- **THEN** its synthesized default uses `Auth = { Type = "none" }`
