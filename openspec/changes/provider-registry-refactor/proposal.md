## Why

`ProviderRegistry` has five distinct responsibilities — config storage, client lifecycle, pending-switch state, client construction for three hard-coded SDKs, and protocol event assembly — making it closed to new providers and difficult to test. Splitting out a `IProviderFactory` interface and a dedicated `Daemon.Providers` project makes adding new LLM providers a matter of adding a file rather than modifying Core, and cleans up the remaining structural issues at the same time.

## What Changes

- **New project `Daemon.Providers`** — contains `OpenAiProviderFactory`, `AnthropicProviderFactory`, and `GeminiProviderFactory`, each implementing `IProviderFactory` (defined in `Daemon.Core`). `Daemon.Core` drops its direct dependencies on `OpenAI`, `Anthropic.SDK`, and `GeminiDotnet`.
- **New `IProviderFactory` interface** — `string AdapterName`, `ChatClientCapabilities GetCapabilities(string modelId)`, and `ValueTask<IChatClient> CreateAsync(ProviderConfig, string? apiKey, CancellationToken)`. Registered as `IEnumerable<IProviderFactory>` in DI; `ProviderRegistry` resolves the right factory by `AdapterName`.
- **New `ChatClientCapabilities` class** — replaces the static config bools (`toolCalling`, `reasoning`). Each factory provides per-model-id defaults. Clients returned by `CreateAsync` also expose capabilities via `GetService(typeof(ChatClientCapabilities))`, following the `ChatClientMetadata` pattern from `Microsoft.Extensions.AI`.
- **`ProviderRegistry` simplified** — no SDK imports; delegates client construction entirely to `IProviderFactory`; `CurrentSupportsToolCalling` / `CurrentSupportsReasoning` probe `ChatClientCapabilities` via the hybrid factory/client path.
- **BREAKING — `CommitPendingSwitch` return type changes** — returns `ProviderSwitchResult` (a plain Core record) instead of `ProviderSwitchedEvent`. `TurnHandler` maps the result to the protocol event. Removes `Daemon.Protocol` dependency from `IProviderRegistry`.
- **BREAKING — `SetModel` added as a separate operation** — `SetProvider(name)` and `SetModel(modelId)` are queued independently. `SetModel` validates the model ID against the current/pending provider's known model list. The existing `SetProvider(name, modelId?)` overload is removed.
- **Static capability config fields removed** — `toolCalling`, `reasoning` booleans removed from `appsettings.json` provider config; capabilities are now owned by `IProviderFactory` implementations.

## Capabilities

### New Capabilities

- `provider-factories`: `IProviderFactory` interface, `ChatClientCapabilities` class, and the three factory implementations in `Daemon.Providers`

### Modified Capabilities

- `provider-registry`: `ProviderRegistry` simplified; `CommitPendingSwitch` return type changes; `SetProvider`/`SetModel` split; capability booleans removed from config

## Impact

- **`src/Daemon.Core/`** — `IProviderFactory.cs` and `ChatClientCapabilities.cs` added; `ProviderRegistry` rewritten; `IProviderRegistry` updated (`CommitPendingSwitch` return type, `SetModel` added, `SetProvider` signature simplified); `DaemonServiceExtensions.AddDaemonCore()` updated to register factories from `Daemon.Providers`
- **`src/Daemon.Providers/`** — new project; three factory implementations; SDK NuGet references move here from `Daemon.Core`
- **`src/Daemon.Core/Rpc/TurnHandler.cs`** — maps `ProviderSwitchResult` → `ProviderSwitchedEvent`
- **`appsettings.json`** — `capabilities.toolCalling` and `capabilities.reasoning` fields removed
- **`test/Daemon.Core.Tests/`** — `ProviderRegistry` tests updated; factory can be mocked via `IProviderFactory`
- **`test/Daemon.Providers.Tests/`** — new test project for factory implementations
- Existing provider configuration files require removal of `capabilities.toolCalling` / `capabilities.reasoning` fields
