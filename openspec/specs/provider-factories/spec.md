## ADDED Requirements

### Requirement: IProviderFactory interface for pluggable client construction
The system SHALL define `IProviderFactory` in `Daemon.Core` with three members: `string AdapterName`, `ChatClientCapabilities GetCapabilities(string modelId)`, and `ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken)`. `ProviderRegistry` SHALL resolve the correct factory by matching `ProviderConfig.Adapter` against `IProviderFactory.AdapterName` (case-insensitive).

#### Scenario: Known adapter resolves to correct factory
- **WHEN** a `ProviderConfig` with `Adapter = "anthropic"` is active
- **THEN** `ProviderRegistry` uses the `AnthropicProviderFactory` to create the client

#### Scenario: Unknown adapter throws at startup
- **WHEN** a `ProviderConfig` with an `Adapter` value that matches no registered factory is present
- **THEN** `ProviderRegistry` throws `InvalidOperationException` during initialisation with a message naming the unknown adapter

### Requirement: Three built-in factory implementations in Daemon.Providers
The `Daemon.Providers` project SHALL contain `OpenAiProviderFactory`, `AnthropicProviderFactory`, and `GeminiProviderFactory`, each implementing `IProviderFactory`. `Daemon.Core` SHALL carry no direct NuGet references to `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet` after this change.

#### Scenario: Daemon.Core has no SDK references
- **WHEN** the `Daemon.Core.csproj` is inspected
- **THEN** it contains no `PackageReference` for `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet`

#### Scenario: Factories registered at startup
- **WHEN** the host starts
- **THEN** all three factory implementations are registered as `IProviderFactory` in DI before the first turn

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

### Requirement: Daemon.Providers has no dependency on Daemon.Core internals
`Daemon.Providers` SHALL reference `Daemon.Core` only for `IProviderFactory`, `ProviderConfig`, and `ChatClientCapabilities`. It SHALL NOT reference any other `Daemon.Core` type.

#### Scenario: Dependency graph is clean
- **WHEN** the solution dependency graph is inspected
- **THEN** `Daemon.Providers` references `Daemon.Core` and the three LLM SDKs; it references nothing else in the daemon solution
