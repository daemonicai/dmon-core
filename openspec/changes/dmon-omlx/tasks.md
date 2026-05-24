# Tasks: dmon-omlx

## Group 1 — Project scaffold

**Goal:** Create the `extensions/Dmon.Extensions.Omlx/` project, add it to the solution, and wire up dependencies.

- [ ] Create `extensions/Dmon.Extensions.Omlx/Dmon.Extensions.Omlx.csproj` targeting `net10.0`; reference `Dmon.Abstractions` and the `OpenAI` NuGet package; set `TreatWarningsAsErrors=true`
- [ ] Add `extensions/Dmon.Extensions.Omlx/` project to `Daemon.slnx`
- [ ] Create `tests/Dmon.Extensions.Omlx.Tests/` xUnit test project; reference the extension project; add to `Daemon.slnx`
- [ ] Confirm `dotnet build` is clean

## Group 2 — Configuration

**Goal:** Implement `OmlxConfig` — the single source of truth for base URL and API key.

- [ ] Implement `OmlxConfig` sealed record: `BaseUrl` (default `http://localhost:8666`) and `ApiKey` (default empty string)
- [ ] Implement `OmlxConfig.FromEnvironment()` static factory: reads `OMLX_BASE_URL` and `OMLX_API_KEY` env vars, falls back to defaults
- [ ] Unit tests: env var override for base URL; env var override for API key; defaults when env vars absent

## Group 3 — Auth handler and provider factory

**Goal:** Implement the custom HTTP auth and the `IProviderFactory`.

- [ ] Implement `OmlxAuthHandler : DelegatingHandler` — adds `x-api-key: {key}` header when key is non-empty; omits the header otherwise; never sends `Authorization`
- [ ] Implement `OmlxProviderFactory : IProviderFactory` — `AdapterName = "omlx"`, `DefaultModelId = string.Empty`; `CreateAsync` builds `OpenAIClientOptions` with custom endpoint + `OmlxAuthHandler`; wraps `OpenAI.Chat.ChatClient.AsIChatClient()` in `CapabilitiesDecorator`
- [ ] `OmlxProviderFactory.GetCapabilities(modelId)` delegates to `OmlxCapabilityHeuristic.Infer(modelId)`
- [ ] Implement `OmlxCapabilityHeuristic` static class with `Infer(string modelId)`: pattern matching per spec (embed/rerank → no tools; qwen3/thinking/r1/reason → tools+reasoning; -it-/instruct/-chat → tools; vlm/vision/-vl- → tools; else → conservative defaults)
- [ ] Unit tests: `OmlxAuthHandler` injects header with key, omits with empty key, no Authorization header; `OmlxCapabilityHeuristic.Infer` for each pattern branch including unrecognised

## Group 4 — Provider extension

**Goal:** Implement `OmlxProviderExtension : IProviderExtension`.

- [ ] Implement `OmlxProviderExtension`: constructor accepts optional `OmlxConfig` (falls back to `OmlxConfig.FromEnvironment()`); exposes `ProviderName = "oMLX"`
- [ ] Implement `IsApplicable()`: `RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64`; catch all exceptions → return `false`
- [ ] Implement `IsRunningAsync()`: `GET {BaseUrl}/v1/models` with `x-api-key` header; 2 s internal timeout; return `true` iff HTTP 200 and any entry has `owned_by == "omlx"`; catch all exceptions → return `false`
- [ ] Implement `EnsureRunningAsync()`: no-op if already running; launch `open -a oMLX` via `Process.Start`; poll `IsRunningAsync()` every 1 s up to timeout (default 30 s); throw `TimeoutException` if timeout elapses
- [ ] Implement `ListModelsAsync()`: `GET {BaseUrl}/v1/models`; map each entry to `new ModelInfo { Id = entry.id, Capabilities = OmlxCapabilityHeuristic.Infer(entry.id) }`; return empty list on any error
- [ ] Implement `CreateFactory()`: return `new OmlxProviderFactory(config)`
- [ ] Unit tests: `IsApplicable()` returns correct value per platform/arch (use `RuntimeInformation` abstraction or test directly); `IsRunningAsync()` true on 200+owned_by, false on wrong owned_by, false on connection refused; `ListModelsAsync()` maps models correctly, returns empty on error; `EnsureRunningAsync()` no-ops when running, throws TimeoutException when server never responds
- [ ] Confirm `dotnet build` and `dotnet test` are clean
