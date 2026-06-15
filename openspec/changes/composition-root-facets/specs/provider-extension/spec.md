## ADDED Requirements

### Requirement: Provider extensions have a composition entry point
A provider extension (`IProviderExtension`) SHALL be composed into the host through the `IProviderRegistration` facet, closing the ADR-019 orphan where the deleted dynamic loader was the type's only route into the host. The host builder SHALL expose `AddProvider<T>()` (where `T : IProviderExtension`) as the primitive registration verb, implemented as `Services.AddSingleton<IProviderExtension, T>()`. Each first-party local-inference provider package SHALL additionally ship a fluent `Use<Provider>` verb that calls `AddProvider<T>()` and selects the provider's model via `UseModel`. There SHALL be no separate post-build or dynamic-loading registration path for provider extensions.

#### Scenario: AddProvider registers the extension as a DI singleton
- **WHEN** `AddProvider<LlamaCppProvider>()` is invoked on the host builder
- **THEN** `LlamaCppProvider` is registered as an `IProviderExtension` singleton in the service collection and no dynamic loader is consulted

#### Scenario: Use verb composes a local provider
- **WHEN** a `Dmon.cs` composition root calls `UseLlamaCpp("gemma")`
- **THEN** the verb calls `AddProvider<LlamaCppProvider>()` and selects model `llamacpp/gemma`, with no edit to `Dmon.Core` required to add the provider

### Requirement: Each provider ships as its own granular implementation package
Each local-inference provider SHALL ship as its own granular implementation package named `Dmon.Providers.<Name>`, referencing `Dmon.Abstractions` plus its vendor SDK, and nothing else in the daemon solution. The package's fluent `Use<Provider>` verb SHALL live in the `Dmon.Hosting` namespace, so that adding the provider to an authored `Dmon.cs` is exactly a `#:package` line plus the verb appearing (no extra `using`). First-party and third-party provider packages SHALL be structurally identical.

#### Scenario: Provider package is self-contained
- **WHEN** the dependency graph of a `Dmon.Providers.<Name>` package is inspected
- **THEN** it references `Dmon.Abstractions` and its own vendor SDK only, and carries its `Use<Provider>` verb

#### Scenario: Verb appears via the Dmon.Hosting namespace
- **WHEN** an authored `Dmon.cs` (which already imports `Dmon.Hosting`) adds a `#:package Dmon.Providers.<Name>` line
- **THEN** the package's `Use<Provider>` verb is available with no additional `using` directive

### Requirement: Use verb prefix equals the registered provider name
The `Use<Provider>` verb's name prefix SHALL equal the provider's registered `ProviderName` (the model-reference contract). The verb SHALL select a model whose reference is `<ProviderName>/<model>`, so that the prefix passed to `UseModel` matches the key under which the registry stores the provider.

#### Scenario: Verb prefix matches the model-ref provider segment
- **WHEN** `UseLlamaCpp("gemma")` is called and `LlamaCppProvider.ProviderName` is `"llamacpp"`
- **THEN** the selected model reference is `"llamacpp/gemma"`, whose provider segment equals `ProviderName`

#### Scenario: Mismatched prefix is an author error
- **WHEN** a provider's `Use<Provider>` verb would select a model reference whose provider segment does not equal the registered `ProviderName`
- **THEN** the model reference cannot resolve the provider in the registry, surfacing the mismatch

## MODIFIED Requirements

### Requirement: Extension lifecycle follows the registered pipeline
A provider extension is composed at author time and routed into the registry at **build time** by DI-discovery (ADR-022 D5), not by a runtime dynamic loader. When the host is built, it SHALL enumerate `IEnumerable<IProviderExtension>` from the container and route each through the existing `IProviderRegistry.RegisterExtensionAsync`, gated by `IsApplicable()`, following the steps below:

1. The extension is registered as an `IProviderExtension` singleton via `AddProvider<T>()` (typically through a `Use<Provider>` verb) in the composition root.
2. At build, the host enumerates all registered `IProviderExtension` instances from DI.
3. For each, `IsApplicable()` is called: if `false`, a `Warning` log is emitted and the provider is not registered in `IProviderRegistry`.
4. If `true`, `IProviderRegistry.RegisterExtensionAsync(extension)` is called, which calls `CreateFactory()`, calls `ListModelsAsync()` to obtain the `DefaultModelId`, synthesises a `ProviderConfig`, and registers the factory and config.

#### Scenario: Applicable extension is routed at build time
- **WHEN** the host is built and a registered `IProviderExtension` returns `true` from `IsApplicable()`
- **THEN** the host enumerates it from DI and calls `IProviderRegistry.RegisterExtensionAsync`, registering the provider

#### Scenario: Inapplicable extension is skipped during DI-discovery
- **WHEN** the host is built and a registered `IProviderExtension` returns `false` from `IsApplicable()`
- **THEN** a `Warning` log is emitted and the provider is not registered in `IProviderRegistry`

### Requirement: Discovery and packaging constraints
A package that contributes an `IProviderExtension` SHALL ship as a granular implementation package (`Dmon.Providers.<Name>` for first-party; `<Owner>.Dmon.<Name>` recommended for third-party), referencing `Dmon.Abstractions` plus its vendor SDK. A single package MAY contribute both an `IToolExtension` (tools) and an `IProviderExtension` (a provider); both are composed independently through their respective facet verbs (`AddToolExtension`, `AddProvider`). The provider's `Use<Provider>` verb SHALL live in the `Dmon.Hosting` namespace.

#### Scenario: Provider package follows the naming convention
- **WHEN** a first-party provider package is published
- **THEN** it is named `Dmon.Providers.<Name>` and exposes a `Use<Provider>` verb in the `Dmon.Hosting` namespace

#### Scenario: Dual-purpose package composes both extension kinds
- **WHEN** a package contributes both an `IToolExtension` and an `IProviderExtension`
- **THEN** the tool extension is composed via `AddToolExtension` and the provider via `AddProvider`/`Use<Provider>`, and both are registered independently
