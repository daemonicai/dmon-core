## 1. Foundation — Project Setup

- [ ] 1.1 Create `src/Daemon.Providers/Daemon.Providers.csproj` referencing `Daemon.Core`, `OpenAI`, `Anthropic.SDK`, and `GeminiDotnet`; add to `Daemon.slnx`
- [ ] 1.2 Create `test/Daemon.Providers.Tests/Daemon.Providers.Tests.csproj` referencing `Daemon.Providers`, `xunit`, and `Microsoft.NET.Test.Sdk`; add to `Daemon.slnx`
- [ ] 1.3 Add a project reference from `Daemon.Core` to `Daemon.Providers` (for startup registration in `DaemonServiceExtensions`)
- [ ] 1.4 Remove `OpenAI`, `Anthropic.SDK`, and `GeminiDotnet` `PackageReference` entries from `Daemon.Core.csproj`

## 2. Core Interfaces and Types

- [ ] 2.1 Create `IProviderFactory` in `Daemon.Core/Providers/IProviderFactory.cs` with `string AdapterName`, `ChatClientCapabilities GetCapabilities(string modelId)`, and `ValueTask<IChatClient> CreateAsync(ProviderConfig, string? apiKey, CancellationToken)`
- [ ] 2.2 Create `ChatClientCapabilities` in `Daemon.Core/Providers/ChatClientCapabilities.cs` — sealed class with `bool SupportsToolCalling`, `bool SupportsReasoning`, `int ContextWindow`, `int MaxTokens`
- [ ] 2.3 Create `ProviderSwitchResult` in `Daemon.Core/Providers/ProviderSwitchResult.cs` — `sealed record ProviderSwitchResult(string ProviderName, string ModelId)`
- [ ] 2.4 Add `void SetModel(string modelId)` to `IProviderRegistry`; change `CommitPendingSwitch` return type from `ProviderSwitchedEvent?` to `ProviderSwitchResult?`; simplify `SetProvider` to `void SetProvider(string name)` (remove optional `modelId` parameter)

## 3. Factory Implementations

- [ ] 3.1 Implement `OpenAiProviderFactory` in `Daemon.Providers` — `AdapterName = "openai"`; `GetCapabilities` returns per-model-id defaults for known GPT-4o / GPT-4 / o1 / o3 family models; `CreateAsync` contains the `CreateOpenAiClient` logic moved from `ProviderRegistry`; inner class `CapabilitiesDecorator` wraps the returned client
- [ ] 3.2 Implement `AnthropicProviderFactory` in `Daemon.Providers` — `AdapterName = "anthropic"`; `GetCapabilities` returns per-model-id defaults for known Claude 3 / Claude 4 family models; `CreateAsync` contains the `CreateAnthropicClient` logic moved from `ProviderRegistry`; inner `CapabilitiesDecorator` wraps the returned client
- [ ] 3.3 Implement `GeminiProviderFactory` in `Daemon.Providers` — `AdapterName = "gemini"`; `GetCapabilities` returns per-model-id defaults for known Gemini 1.5 / 2.x models; `CreateAsync` contains the `CreateGeminiClient` logic moved from `ProviderRegistry`; inner `CapabilitiesDecorator` wraps the returned client
- [ ] 3.4 Each `CapabilitiesDecorator` (private inner class per factory) SHALL implement `IChatClient`, forward all members to the inner client, and return the capabilities instance when `GetService(typeof(ChatClientCapabilities))` is called

## 4. ProviderRegistry Rewrite

- [ ] 4.1 Rewrite `ProviderRegistry` to inject `IEnumerable<IProviderFactory>` instead of SDK clients; build a `Dictionary<string, IProviderFactory>` keyed by `AdapterName` at construction; throw `InvalidOperationException` for any `ProviderConfig.Adapter` with no matching factory
- [ ] 4.2 Replace `CreateClientAsync` switch with `_factories[config.Adapter].CreateAsync(config, apiKey, ct)` call
- [ ] 4.3 Implement `SetModel(string modelId)` — store as `_pendingModelId`; log a warning if the model is not found in the factory's `GetCapabilities` lookup (do not block)
- [ ] 4.4 Update `CommitPendingSwitch` to return `ProviderSwitchResult?` instead of `ProviderSwitchedEvent?`; apply both `_pendingIndex` and `_pendingModelId` atomically
- [ ] 4.5 Rewrite `CurrentSupportsToolCalling` and `CurrentSupportsReasoning` to use the hybrid path: `_activeClient?.GetService(typeof(ChatClientCapabilities)) as ChatClientCapabilities ?? _factories[config.Adapter].GetCapabilities(config.DefaultModelId ?? "")`
- [ ] 4.6 Remove `IBashCompositeDetector`, `IDenylistChecker` constructor parameters if still present (these moved to `Daemon.BuiltinTools` in the `daemon-builtin-tools` change); remove all static `CreateOpenAiClient`, `CreateAnthropicClient`, `CreateGeminiClient` methods

## 5. TurnHandler and Wiring

- [ ] 5.1 Update `TurnHandler.RunTurnAsync` to map `ProviderSwitchResult` → `ProviderSwitchedEvent` after calling `CommitPendingSwitch`
- [ ] 5.2 Update `CommandDispatcher` (or `ModelHandler`) to call `SetProvider(name)` and `SetModel(modelId)` separately when handling the `model.set` RPC command
- [ ] 5.3 Register all three factories as `IProviderFactory` in `DaemonServiceExtensions.AddDaemonCore()` (or a new `AddDaemonProviders()` extension in `Daemon.Providers`)

## 6. Configuration Cleanup

- [ ] 6.1 Remove `capabilities.toolCalling` and `capabilities.reasoning` fields from `ProviderConfig`, `ProviderCapabilities`, and `ProviderConfigLoader` — `ProviderCapabilities` may retain `ContextWindow` and `MaxTokens` if still used elsewhere, or be removed entirely if all fields are now factory-owned
- [ ] 6.2 Update `appsettings.json` (and any test fixture config files) to remove `capabilities.toolCalling` and `capabilities.reasoning` from all provider entries

## 7. Tests

- [ ] 7.1 Write unit tests for `AnthropicProviderFactory.GetCapabilities` — known Claude 4 model returns `SupportsReasoning = true`; unknown model returns conservative defaults
- [ ] 7.2 Write unit tests for `OpenAiProviderFactory.GetCapabilities` — known o1/o3 model returns `SupportsReasoning = true`; unknown model returns conservative defaults
- [ ] 7.3 Write unit tests for `ProviderRegistry` with mock `IProviderFactory` — unknown adapter throws; `CurrentSupportsToolCalling` reads from `ChatClientCapabilities` via `GetService`; `CommitPendingSwitch` returns `ProviderSwitchResult`; `SetModel` queues independently of `SetProvider`
- [ ] 7.4 Write unit test verifying `CapabilitiesDecorator` forwards `GetService(typeof(ChatClientCapabilities))` correctly and forwards all other service queries to the inner client
- [ ] 7.5 Verify build: `dotnet build` succeeds with zero warnings; `dotnet test` passes all tests; `Daemon.Core.csproj` contains no references to `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet`
