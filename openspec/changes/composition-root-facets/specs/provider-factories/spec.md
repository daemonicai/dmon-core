## ADDED Requirements

### Requirement: Cloud and local providers are composed symmetrically via Use verbs
Provider composition SHALL be symmetric (Option B): cloud providers (`IProviderFactory`) and local providers (`IProviderExtension`) are BOTH composed by the user through `Use<Provider>` verbs on `IProviderRegistration`. Nothing SHALL be baked into `dmoncore`. The verb grammar SHALL hide the shape difference — a `Use<CloudProvider>` registers a factory; a `Use<LocalProvider>` registers a lifecycle extension — but both are author-time composition verbs in the `Dmon.Hosting` namespace. The stock default `Dmon.cs` SHALL list its providers explicitly (e.g. `UseAnthropic().UseOpenAI().UseGemini().UseOllama()`).

#### Scenario: Cloud provider composed with a Use verb
- **WHEN** a `Dmon.cs` composition root calls `UseOpenAI("gpt5.5")`
- **THEN** the verb registers the OpenAI `IProviderFactory` and selects the model, with no provider baked into `dmoncore`

#### Scenario: Stock default lists providers explicitly
- **WHEN** the scaffolded default `Dmon.cs` is read
- **THEN** it composes its cloud providers as an explicit, editable list of `Use<Provider>` verbs rather than an opaque aggregate

### Requirement: Cloud providers register unconditionally
Cloud providers SHALL register unconditionally — they have no `IsApplicable()` lifecycle gate (that gate belongs to local `IProviderExtension` providers). A `Use<CloudProvider>` verb SHALL register the factory whenever it is invoked. Model selection among the registered cloud providers SHALL remain config/wizard-driven (ADR-005/007), unchanged by this change.

#### Scenario: Cloud factory registered without an applicability check
- **WHEN** `UseAnthropic()` is invoked in a composition root
- **THEN** the Anthropic `IProviderFactory` is registered unconditionally, with no `IsApplicable()` gate consulted

#### Scenario: Model selection stays config/wizard-driven
- **WHEN** multiple cloud providers are composed
- **THEN** the active provider and model are selected through configuration or the setup wizard, not by an applicability lifecycle

## MODIFIED Requirements

### Requirement: Three built-in factory implementations in Dmon.Providers
The four built-in cloud factories — `OpenAiProviderFactory`, `AnthropicProviderFactory`, `GeminiProviderFactory`, and the Ollama factory — SHALL each move out of `Dmon.Core`/`Dmon.Providers` into their own granular per-provider implementation packages (`Dmon.Providers.OpenAI`, `Dmon.Providers.Anthropic`, `Dmon.Providers.Gemini`, `Dmon.Providers.Ollama`), each referencing `Dmon.Abstractions` plus its own vendor SDK and exposing its `Use<Provider>` verb in the `Dmon.Hosting` namespace. `Dmon.Core` SHALL become **vendor-SDK-free**: it SHALL carry no direct NuGet references to any provider vendor SDK. Each factory SHALL still implement `IProviderFactory` including `GetAvailableModelsAsync`.

#### Scenario: Dmon.Core has no SDK references
- **WHEN** the `Dmon.Core.csproj` is inspected
- **THEN** it contains no `PackageReference` for any provider vendor SDK (OpenAI, Anthropic, Gemini, Ollama)

#### Scenario: Each cloud factory lives in its own package
- **WHEN** the solution package set is inspected
- **THEN** each built-in cloud factory resides in its own `Dmon.Providers.<Name>` package referencing `Dmon.Abstractions` and that provider's vendor SDK

#### Scenario: Factories composed via Use verbs, not baked
- **WHEN** the host starts for a `Dmon.cs` that composes cloud providers
- **THEN** the factories present are exactly those registered by the `Use<Provider>` verbs the composition root invoked, not a fixed baked-in set

#### Scenario: Each factory returns a non-empty model list
- **WHEN** `GetAvailableModelsAsync(null, CancellationToken.None)` is called on any built-in factory
- **THEN** the result is non-null and contains at least one `ModelInfo` (the static fallback)

## REMOVED Requirements

### Requirement: AddDmonProviders bakes the built-in factories into the host
**Reason**: ADR-022 D7 / ADR-023 D1 — provider composition becomes symmetric (Option B); nothing is baked into `dmoncore`. `AddDmonProviders` registered the cloud factories as DI singletons inside the engine and pulled the vendor SDKs into `Dmon.Core`, which is incompatible with a vendor-SDK-free engine.
**Migration**: Compose each cloud provider explicitly via its `Use<Provider>` verb in the `Dmon.cs` composition root (e.g. `UseAnthropic().UseOpenAI().UseGemini().UseOllama()`), referencing the corresponding `Dmon.Providers.<Name>` `#:package`. A blessed `AddDefaultProviders()` convenience verb MAY provide the equivalent aggregate, but is not what the scaffold emits.

#### Scenario: AddDmonProviders is no longer available
- **WHEN** a composition root attempts to call `AddDmonProviders()`
- **THEN** no such method exists; providers must be composed via per-package `Use<Provider>` verbs
