## 1. Discovery & audit

- [ ] 1.1 Enumerate every reference to `IDmonExtension`, `Dmon.Extensions` (package + namespace), `DmonMiddlewareAttribute`, and `DmonAIFunctionFactory` across the solution (src, samples, tests, `Dmon.cs` roots, docs) — produce the rename worklist.
- [ ] 1.2 Enumerate every reference to the profile subsystem — `IAgentProfileResolver`, `AgentProfile`, `ProfilesConfigReader`, `EffectiveProfileSetResolver`, `AgentProfileContext`, `WithProfile`, `ProfileOverrideResolver`, and `profile` in the gateway/RPC surface — to scope Group 7.
- [ ] 1.3 Enumerate `Dmon.Core`'s direct vendor-SDK usages (`Anthropic`, `GeminiDotnet`, `Microsoft.Extensions.AI.OpenAI`, `OllamaSharp`) and the `PrivateAssets="all"` provider/builtin project refs, to scope Groups 4–5.
- [ ] 1.4 Confirm the setup wizard, model listing, and `IProviderRegistry` consume providers only through abstractions (no concrete-provider coupling that the package split would break).

## 2. Contracts collapse & rename (atomic, build-green)

- [ ] 2.1 Move `IToolExtension` (renamed from `IDmonExtension`), `IDmonMiddleware`, `DmonMiddlewareAttribute`, and `DmonAIFunctionFactory` from `Dmon.Extensions` into `Dmon.Abstractions`; delete the `Dmon.Extensions` project and remove it from `Dmon.slnx`.
- [ ] 2.2 Declare the registration facets `IProviderRegistration`, `IToolRegistration`, `IMiddlewareRegistration` and the aggregate `IDmonHostBuilder { IServiceCollection Services; IConfigurationManager Configuration; }` (implementing the three facets) in `Dmon.Abstractions`.
- [ ] 2.3 Declare `IChatClientFactory` (`ValueTask<IChatClient> CreateAsync(CancellationToken)`) in `Dmon.Abstractions`.
- [ ] 2.4 Update every consumer from Group 1.1 to the new names/namespace so the whole solution compiles; remove `Dmon.Extensions` package/project references everywhere (point them at `Dmon.Abstractions`).
- [ ] 2.5 Update the sample extension and both `Dmon.cs` roots to the new contract names (keep them compiling; full verb migration lands in later groups).
- [ ] 2.6 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.

## 3. Faceted builder, verb grammar & DI-discovery

- [ ] 3.1 Make `DmonHostBuilder` implement `IDmonHostBuilder` (expose `Services`/`Configuration`); add the parameterless `CreateBuilder()` overload.
- [ ] 3.2 Implement the blessed verbs as self-type generic extension methods in the `Dmon.Hosting` namespace: `AddToolExtension<T>`/instance, `AddMiddleware<T>(priority?)`/instance, `AddProvider<T>`/instance, `UseModel`; migrate `WithModel`→`UseModel`, `AddExtension`→`AddToolExtension`.
- [ ] 3.3 `AddToolExtension<T>` drops the `new()` constraint and instantiates via `ActivatorUtilities` (DI-constructed tools).
- [ ] 3.4 Switch `AddDmonCore`/registries to **build-time DI-discovery**: enumerate `IEnumerable<IToolExtension>`/`IDmonMiddleware`/`IProviderExtension` and route into `IToolRegistry`/`IMiddlewareRegistry`/`IProviderRegistry.RegisterExtensionAsync` (provider gated by `IsApplicable`); delete the post-build manual loops in `DmonHostBuilder`. Preserve the `TryAdd`-yields-to-builder ordering (stdio `TextWriter`) and the permission-mode override decorator.
- [ ] 3.5 Update the composed sample to use `AddToolExtension`; add/adjust tests covering flat + faceted chaining and DI-constructed tool instantiation.
- [ ] 3.6 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.

## 4. Provider split & Option-B symmetry

- [ ] 4.1 Extract `Dmon.Providers.Anthropic`, `Dmon.Providers.OpenAI`, `Dmon.Providers.Gemini`, `Dmon.Providers.Ollama` packages — each references `Dmon.Abstractions` + its vendor SDK and ships a `Use<Provider>` verb in `Dmon.Hosting`; the verb prefix equals the registered `ProviderName`/`AdapterName`.
- [ ] 4.2 Remove the four vendor-SDK package references and the `PrivateAssets="all"` provider project refs from `Dmon.Core`; delete `AddDmonProviders`. Verify `dmoncore` no longer compiles against any vendor SDK.
- [ ] 4.3 Wire provider DI-discovery for both shapes (`IProviderFactory` cloud + `IProviderExtension` local) through the facet verbs.
- [ ] 4.4 Update `default-core/Dmon.cs` to compose providers explicitly (`UseAnthropic().UseOpenAI().UseGemini().UseOllama()`); add the optional `.AddDefaultProviders()` sugar.
- [ ] 4.5 Update tests (provider registration, registry enumeration, setup wizard, model listing) for the package-supplied factories.
- [ ] 4.6 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.

## 5. Sub-agent provider isolation & `IChatClientFactory`

- [ ] 5.1 Implement the isolated `IProviderRegistration` materialization → captured `IChatClientFactory`; it MUST NOT touch `IProviderRegistry`.
- [ ] 5.2 Add the `Action<IProviderRegistration>` tool-registration path (provider-agnostic base form; optional bundling convenience verb pattern documented).
- [ ] 5.3 Implement validation/timing: structural validity (one provider verb + a model) at `Build()` (malformed → build failure); lazy credential resolution + client construction at first `CreateAsync` (`InvalidOperationException` names the missing env var); allow client memoization.
- [ ] 5.4 Tests: isolation from `IProviderRegistry`, build-time structural failure, lazy missing-key error, memoization, single-turn usage.
- [ ] 5.5 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.

## 6. System prompt as a plain string (no persona)

- [ ] 6.1 Resolve the system prompt from `IConfiguration["systemPrompt"]` as the base, overridable by `UseSystemPrompt(string)` (replace) and `AppendToSystemPrompt(string)` (ordered append); `final = base + ordered appends`, no hidden scaffolding (tools ride `ChatOptions`).
- [ ] 6.2 Keep the raw-DI escape hatch (`Services.AddSingleton<ISystemPromptBuilder>(…)`); update `ISystemPromptBuilder` wiring to read the new sources.
- [ ] 6.3 Tests: precedence (config < `UseSystemPrompt`), append ordering, default-when-unset, escape-hatch override.
- [ ] 6.4 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.

## 7. Profile-subsystem demolition & `agent` selection

- [ ] 7.1 Delete the profile subsystem (`IAgentProfileResolver`, `AgentProfile`, `ProfilesConfigReader`, `EffectiveProfileSetResolver`, `AgentProfileContext`, `WithProfile`, `ProfileOverrideResolver`); retain `PermissionMode` behind `WithPermissionMode`.
- [ ] 7.2 Add the `UseAssets(path)` verb wrapping `ISessionAssetProvisioner`; an agent is its `.cs` (default = root `Dmon.cs`; named = `.dmon/agents/<name>.cs`).
- [ ] 7.3 Change the gateway `createSession` contract `profile`→`agent` (name of a `.cs` resolved under the gateway workspace root); update the handler, the protocol DTOs, and `openspec/specs` protocol schema delta if affected.
- [ ] 7.4 Update/remove profile-related tests; add tests for `agent` selection and `UseAssets`.
- [ ] 7.5 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.

## 8. Builtin-tools package, scaffold & verification

- [ ] 8.1 Publish `Dmon.Tools.Builtin` (references only `Dmon.Abstractions`/`Dmon.Protocol`) exposing `.AddBuiltinTools()`; remove the hard-wired builtin-tools registration from the engine.
- [ ] 8.2 Update the scaffolded `Dmon.cs` (`dmon init`) and the prebuilt stock default to `#:package` `dmoncore` + the cloud provider packages + `Dmon.Tools.Builtin` and compose them; confirm a tool-less locked-down composition is valid.
- [ ] 8.3 Set protocol-keyed lockstep versions across contracts + first-party packages; confirm an incompatible `#:package` set fails at `dotnet restore`.
- [ ] 8.4 Human-in-the-loop verification (provide a copy-pasteable recipe): build + run the stock default core and a minimal single-provider `Dmon.cs`, confirm `agentReady` and a tool call work; verify a sub-agent web-search-style tool composes with `Action<IProviderRegistration>`.
- [ ] 8.5 Gate: `make build` clean, `make test` green, `openspec validate composition-root-facets --strict`.
