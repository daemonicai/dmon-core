## ADDED Requirements

### Requirement: IProviderFactory interface for pluggable client construction
The system SHALL define `IProviderFactory` in `Dmon.Abstractions` with four members: `string AdapterName`, `ChatClientCapabilities GetCapabilities(string modelId)`, `ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken)`, and `ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)`. The fourth member SHALL have a default interface implementation that returns an empty list, so existing external implementations continue to compile without modification. `ProviderRegistry` SHALL resolve the correct factory by matching `ProviderConfig.Adapter` against `IProviderFactory.AdapterName` (case-insensitive).

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

#### Scenario: Dmon.Core has no SDK references
- **WHEN** the `Dmon.Core.csproj` is inspected
- **THEN** it contains no `PackageReference` for `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet`

#### Scenario: Factories registered at startup
- **WHEN** the host starts
- **THEN** all three factory implementations are registered as `IProviderFactory` in DI before the first turn

#### Scenario: Each factory returns a non-empty model list
- **WHEN** `GetAvailableModelsAsync(null, CancellationToken.None)` is called on any built-in factory
- **THEN** the result is non-null and contains at least one `ModelInfo` (the static fallback)

### Requirement: ChatClientCapabilities available via GetService
Each `IChatClient` returned by a factory's `CreateAsync` SHALL expose a `ChatClientCapabilities` instance via `GetService(typeof(ChatClientCapabilities))`. Callers who hold only an `IChatClient` reference SHALL be able to probe capabilities without knowing the factory that created it.

#### Scenario: GetService returns capabilities
- **WHEN** `client.GetService(typeof(ChatClientCapabilities))` is called on a client returned by any built-in factory
- **THEN** a non-null `ChatClientCapabilities` instance is returned

#### Scenario: Capabilities forwarded through pipeline middleware
- **WHEN** the client is wrapped in M.E.AI pipeline middleware (e.g. `FunctionInvokingChatClient`)
- **THEN** `GetService(typeof(ChatClientCapabilities))` still returns the capabilities (middleware forwards unknown service queries to the inner client)

### Requirement: IProviderFactory.GetCapabilities provides static per-model-id defaults
`GetCapabilities(string modelId)` SHALL return a `ChatClientCapabilities` instance with correct values for known model IDs. Unknown model IDs SHALL return a conservative default: `SupportsToolCalling = false`, `SupportsReasoning = false`.

#### Scenario: Known model returns correct capabilities
- **WHEN** `AnthropicProviderFactory.GetCapabilities("claude-opus-4-7")` is called
- **THEN** the result has `SupportsToolCalling = true` and `SupportsReasoning = true`

#### Scenario: Unknown model returns conservative default
- **WHEN** `GetCapabilities("unknown-model-xyz")` is called on any factory
- **THEN** the result has `SupportsToolCalling = false` and `SupportsReasoning = false`

### Requirement: Dmon.Providers has no dependency on Dmon.Core internals
`Dmon.Providers` SHALL reference `Dmon.Abstractions` only for `IProviderFactory`, `ProviderConfig`, and `ChatClientCapabilities`. It SHALL NOT reference any other `Dmon.Core` type.

#### Scenario: Dependency graph is clean
- **WHEN** the solution dependency graph is inspected
- **THEN** `Dmon.Providers` references `Dmon.Abstractions` and the three LLM SDKs; it references nothing else in the daemon solution
