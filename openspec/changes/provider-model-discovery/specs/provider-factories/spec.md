## MODIFIED Requirements

### Requirement: IProviderFactory interface for pluggable client construction
The system SHALL define `IProviderFactory` in `Dmon.Abstractions` with four members: `string AdapterName`, `ChatClientCapabilities GetCapabilities(string modelId)`, `ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken)`, and `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)`. The fourth member SHALL have a default interface implementation that returns a static fallback list, so existing external implementations continue to compile without modification. `ProviderRegistry` SHALL resolve the correct factory by matching `ProviderConfig.Adapter` against `IProviderFactory.AdapterName` (case-insensitive).

#### Scenario: Known adapter resolves to correct factory
- **WHEN** a `ProviderConfig` with `Adapter = "anthropic"` is active
- **THEN** `ProviderRegistry` uses the `AnthropicProviderFactory` to create the client

#### Scenario: Unknown adapter throws at startup
- **WHEN** a `ProviderConfig` with an `Adapter` value that matches no registered factory is present
- **THEN** `ProviderRegistry` throws `InvalidOperationException` during initialisation with a message naming the unknown adapter

#### Scenario: External implementor compiles without adding the new method
- **WHEN** an external type implements `IProviderFactory` without defining `GetAvailableModelsAsync`
- **THEN** the project compiles successfully because the default interface implementation is used

### Requirement: Three built-in factory implementations in Dmon.Providers
The `Dmon.Providers` project SHALL contain `OpenAiProviderFactory`, `AnthropicProviderFactory`, and `GeminiProviderFactory`, each implementing `IProviderFactory` including `GetAvailableModelsAsync`. `Dmon.Core` SHALL carry no direct NuGet references to `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet` after this change.

#### Scenario: Daemon.Core has no SDK references
- **WHEN** the `Dmon.Core.csproj` is inspected
- **THEN** it contains no `PackageReference` for `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet`

#### Scenario: Factories registered at startup
- **WHEN** the host starts
- **THEN** all three factory implementations are registered as `IProviderFactory` in DI before the first turn

#### Scenario: Each factory returns a non-empty model list
- **WHEN** `GetAvailableModelsAsync(null, CancellationToken.None)` is called on any built-in factory
- **THEN** the result is non-null and contains at least one `ModelInfo` (the static fallback)
