## 1. Project Scaffolding

- [x] 1.1 Create `src/Dmon.Providers.Ollama/Dmon.Providers.Ollama.csproj` targeting `net10.0` with `TreatWarningsAsErrors`, `Nullable enable`, `ImplicitUsings enable`; add `PackageReference` for `OllamaSharp` 5.4.25 and `ProjectReference` to `Dmon.Abstractions`
- [x] 1.2 Add `Dmon.Providers.Ollama` to `Dmon.slnx`

## 2. OllamaProviderFactory

- [x] 2.1 Create `src/Dmon.Providers.Ollama/OllamaProviderFactory.cs` implementing `IProviderFactory` with `AdapterName = "ollama"`, `DisplayName = "Ollama"`, `DefaultModelId = "llama3.2"`, `DefaultEnvVar = "OLLAMA_HOST"`
- [x] 2.2 Implement `GetCapabilities(string modelId)` using the pattern heuristic from the `provider-extension` spec (instruct/chat → tool calling; qwen3/reason/thinking/r1 → reasoning; embed/rerank → neither; unknown → conservative false/false)
- [x] 2.3 Implement `GetAvailableModelsAsync` — construct `OllamaApiClient` from `apiKey` (treated as base URL) or fall back to `http://localhost:11434`; call the appropriate OllamaSharp API to list local models; return empty list on any exception
- [x] 2.4 Implement `GetNextStepAsync` wizard flow: step 1 = `ChooseOneStep` id `"deployment"` (Local / Cloud); step 2 = `TextInputStep` id `"base-url"` with correct default per deployment choice, overridden by `OLLAMA_HOST` env var when set; step 3 = `ChooseOneStep` id `"model"` populated via `GetAvailableModelsAsync`, with a not-reachable hint option when list is empty; step 4 = `WizardCompletedStep`
- [x] 2.5 Implement `CreateAsync` — construct `OllamaApiClient` with `config.BaseUrl` (or `http://localhost:11434` fallback), select `config.DefaultModelId`, wrap in `CapabilitiesDecorator` using `GetCapabilities`

## 3. OllamaProviderExtension

- [x] 3.1 Create `src/Dmon.Providers.Ollama/OllamaProviderExtension.cs` implementing `IProviderExtension` with `ProviderName = "Ollama"` and a constructor that accepts a base URL (defaulting to `http://localhost:11434`)
- [x] 3.2 Implement `IsApplicable()` returning `true`
- [x] 3.3 Implement `IsRunningAsync` — use OllamaSharp to ping the configured base URL with a 2-second timeout; return `false` (do not throw) on any exception or timeout
- [x] 3.4 Implement `EnsureRunningAsync` throwing `NotSupportedException` with message `"Ollama must be started manually. See https://ollama.com for installation instructions."`
- [x] 3.5 Implement `ListModelsAsync` — delegate to `OllamaApiClient`, map results to `ModelInfo` using `GetCapabilities` heuristic
- [x] 3.6 Implement `CreateFactory()` returning `new OllamaProviderFactory()`

## 4. Startup Registration

- [ ] 4.1 In `DaemonServiceExtensions.cs`, add `ProjectReference` to `Dmon.Providers.Ollama` in `Dmon.Core.csproj`
- [ ] 4.2 Register `OllamaProviderFactory` as `IProviderFactory` alongside the other built-in factories in `DaemonServiceExtensions`

## 5. Build Verification

- [ ] 5.1 Run `dotnet build` from the solution root and confirm zero warnings and zero errors
