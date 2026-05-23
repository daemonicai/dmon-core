## 1. Foundation — Project Setup

- [x] 1.1 Create `src/Dmon.Providers/Dmon.Providers.csproj` referencing `Anthropic.SDK`, `GeminiDotnet.Extensions.AI`, and `Microsoft.Extensions.AI.OpenAI`; add to `Dmon.slnx`
- [x] 1.2 Create `test/Dmon.Providers.Tests/Dmon.Providers.Tests.csproj` referencing `Dmon.Providers`, `xunit`, and `Microsoft.NET.Test.Sdk`; add to `Dmon.slnx`
- [x] 1.3 **REVISED (Option B — Dmon.Abstractions):** Create `src/Dmon.Abstractions/Dmon.Abstractions.csproj`; move `ProviderConfig.cs` (and `ProviderAuthConfig`, `ProviderCapabilities`) there; add `Dmon.Abstractions` to `Dmon.slnx`; update `Dmon.Core` to reference `Dmon.Abstractions` and `Dmon.Providers`; update `Dmon.Providers` to reference `Dmon.Abstractions` instead of `Dmon.Core`. Rationale: `Core→Providers→Core` is circular; Abstractions breaks the cycle.
- [x] 1.4 Remove `Anthropic.SDK`, `GeminiDotnet.Extensions.AI`, and `Microsoft.Extensions.AI.OpenAI` `PackageReference` entries from `Dmon.Core.csproj`

## 2. Core Interfaces and Types

- [x] 2.1 Create `IProviderFactory` in `Dmon.Abstractions/Providers/IProviderFactory.cs` with `string AdapterName`, `ChatClientCapabilities GetCapabilities(string modelId)`, and `ValueTask<IChatClient> CreateAsync(ProviderConfig, string? apiKey, CancellationToken)`
- [x] 2.2 Create `ChatClientCapabilities` in `Dmon.Abstractions/Providers/ChatClientCapabilities.cs` — sealed class with `bool SupportsToolCalling`, `bool SupportsReasoning`, `int ContextWindow`, `int MaxTokens`
- [x] 2.3 Create `ProviderSwitchResult` in `Dmon.Abstractions/Providers/ProviderSwitchResult.cs` — `sealed record ProviderSwitchResult(string ProviderName, string ModelId)`
- [x] 2.4 Add `void SetModel(string modelId)` to `IProviderRegistry`; change `CommitPendingSwitch` return type from `ProviderSwitchedEvent?` to `ProviderSwitchResult?`; simplify `SetProvider` to `void SetProvider(string name)` (remove optional `modelId` parameter)

## 3. Factory Implementations

- [x] 3.1 Implement `OpenAiProviderFactory` in `Daemon.Providers` — `AdapterName = "openai"`; `GetCapabilities` returns per-model-id defaults for known GPT-4o / GPT-4 / o1 / o3 family models; `CreateAsync` contains the `CreateOpenAiClient` logic moved from `ProviderRegistry`; inner class `CapabilitiesDecorator` wraps the returned client
- [x] 3.2 Implement `AnthropicProviderFactory` in `Daemon.Providers` — `AdapterName = "anthropic"`; `GetCapabilities` returns per-model-id defaults for known Claude 3 / Claude 4 family models; `CreateAsync` contains the `CreateAnthropicClient` logic moved from `ProviderRegistry`; inner `CapabilitiesDecorator` wraps the returned client
- [x] 3.3 Implement `GeminiProviderFactory` in `Daemon.Providers` — `AdapterName = "gemini"`; `GetCapabilities` returns per-model-id defaults for known Gemini 1.5 / 2.x models; `CreateAsync` contains the `CreateGeminiClient` logic moved from `ProviderRegistry`; inner `CapabilitiesDecorator` wraps the returned client
- [x] 3.4 Each `CapabilitiesDecorator` (private inner class per factory) SHALL implement `IChatClient`, forward all members to the inner client, and return the capabilities instance when `GetService(typeof(ChatClientCapabilities))` is called

## 4. ProviderRegistry Rewrite

- [x] 4.1 Rewrite `ProviderRegistry` to inject `IEnumerable<IProviderFactory>` instead of SDK clients; build a `Dictionary<string, IProviderFactory>` keyed by `AdapterName` at construction; throw `InvalidOperationException` for any `ProviderConfig.Adapter` with no matching factory
- [x] 4.2 Replace `CreateClientAsync` switch with `_factories[config.Adapter].CreateAsync(config, apiKey, ct)` call
- [x] 4.3 Implement `SetModel(string modelId)` — store as `_pendingModelId`; log a warning if the model is not found in the factory's `GetCapabilities` lookup (do not block)
- [x] 4.4 Update `CommitPendingSwitch` to return `ProviderSwitchResult?` instead of `ProviderSwitchedEvent?`; apply both `_pendingIndex` and `_pendingModelId` atomically
- [x] 4.5 Rewrite `CurrentSupportsToolCalling` and `CurrentSupportsReasoning` to use the hybrid path: `_activeClient?.GetService(typeof(ChatClientCapabilities)) as ChatClientCapabilities ?? _factories[config.Adapter].GetCapabilities(config.DefaultModelId ?? "")`
- [x] 4.6 Remove `IBashCompositeDetector`, `IDenylistChecker` constructor parameters if still present (these moved to `Daemon.BuiltinTools` in the `daemon-builtin-tools` change); remove all static `CreateOpenAiClient`, `CreateAnthropicClient`, `CreateGeminiClient` methods

## 5. TurnHandler and Wiring

- [x] 5.1 Update `TurnHandler.RunTurnAsync` to map `ProviderSwitchResult` → `ProviderSwitchedEvent` after calling `CommitPendingSwitch`
- [x] 5.2 Update `CommandDispatcher` (or `ModelHandler`) to call `SetProvider(name)` and `SetModel(modelId)` separately when handling the `model.set` RPC command
- [x] 5.3 Register all three factories as `IProviderFactory` in `DaemonServiceExtensions.AddDmonProviders()` in `Dmon.Core`

## 6. Configuration Cleanup

- [x] 6.1 Remove `capabilities.toolCalling` and `capabilities.reasoning` fields from `ProviderConfig`, `ProviderCapabilities`, and `ProviderConfigLoader` — `ProviderCapabilities` may retain `ContextWindow` and `MaxTokens` if still used elsewhere, or be removed entirely if all fields are now factory-owned
- [x] 6.2 Update `appsettings.json` (and any test fixture config files) to remove `capabilities.toolCalling` and `capabilities.reasoning` from all provider entries

## 7. Tests

- [ ] 7.1 Write unit tests for `AnthropicProviderFactory.GetCapabilities` — known Claude 4 model returns `SupportsReasoning = true`; unknown model returns conservative defaults
- [ ] 7.2 Write unit tests for `OpenAiProviderFactory.GetCapabilities` — known o1/o3 model returns `SupportsReasoning = true`; unknown model returns conservative defaults
- [ ] 7.3 Write unit tests for `ProviderRegistry` with mock `IProviderFactory` — unknown adapter throws; `CurrentSupportsToolCalling` reads from `ChatClientCapabilities` via `GetService`; `CommitPendingSwitch` returns `ProviderSwitchResult`; `SetModel` queues independently of `SetProvider`
- [ ] 7.4 Write unit test verifying `CapabilitiesDecorator` forwards `GetService(typeof(ChatClientCapabilities))` correctly and forwards all other service queries to the inner client
- [ ] 7.5 Verify build: `dotnet build` succeeds with zero warnings; `dotnet test` passes all tests; `Daemon.Core.csproj` contains no references to `OpenAI`, `Anthropic.SDK`, or `GeminiDotnet`
