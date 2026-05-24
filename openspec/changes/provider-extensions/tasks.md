# Tasks: Provider Extensions

> **Dependency note:** Groups 1–3 can proceed independently. The security pipeline from
> `extension-ecosystem` Group 5 (`IExtensionSourceFetcher`, `ExtensionSecurityAnalyser`) runs
> before assembly load and is orthogonal to the routing changes made here. Integration of both
> changes is complete once `extension-ecosystem` is fully archived.

## Group 1 — Core abstractions and ADR

**Goal:** Define the `IProviderExtension` interface and `ModelInfo` record in `Dmon.Abstractions`, and document the design decision in ADR-007.

- [x] Add `ModelInfo` record to `Dmon.Abstractions/Providers/ModelInfo.cs`
- [x] Add `IProviderExtension` interface to `Dmon.Abstractions/Providers/IProviderExtension.cs`
- [x] Write `docs/adrs/ADR-007-provider-extension-lifecycle.md` documenting: `IsApplicable()` semantics (load-time, Warning on false, approval persisted); `EnsureRunningAsync()` gated on ADR-006 prompt; per-runner factory required due to auth variance; capability heuristic from model ID

## Group 2 — Registry support for provider extensions

**Goal:** Allow `ProviderRegistry` to accept runtime-registered providers from extension packages.

- [x] Add `Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default)` to `IProviderRegistry`
- [x] Implement `ProviderRegistry.RegisterExtensionAsync`: call `CreateFactory()`, call `ListModelsAsync()` (pick first model as `DefaultModelId`), synthesise `ProviderConfig` with `Auth.Type = "none"`, store factory + config; replace existing entry if same `ProviderName` is already registered
- [x] `ProviderRegistry.GetAll()` returns built-in configs concatenated with extension-registered configs
- [x] Unit tests: register extension provider → appears in `GetAll()`; `SetProvider` finds it; replacing duplicate name updates the entry

## Group 3 — Extension loader routing

**Goal:** Update the extension loading pipeline to detect `IProviderExtension`, run `IsApplicable()`, and route to the registry.

- [ ] Add `IProviderExtension? ProviderExtension { get; init; }` to `ExtensionLoadResult`
- [ ] Update `NuGetExtensionLoader.DiscoverExtensions` (rename to `DiscoverAll`) to also scan for `IProviderExtension` types; return both tool extensions and provider extensions; a type may implement both
- [ ] Update `ExtensionService` constructor to accept `IProviderRegistry?` (nullable — falls back to Warning if absent)
- [ ] Update `ExtensionService.LoadAsync`: route `ProviderExtension` → call `IsApplicable()` (log Warning + return if false); call `RegisterExtensionAsync` if applicable; relax the "no tools → error" invariant to "no tools AND no provider → error"
- [ ] Add `string? ProviderName { get; init; }` to `ExtensionLoadedEvent`; populate with `extension.ProviderName` when a provider is registered, null otherwise
- [ ] Unit tests: provider extension `IsApplicable()` false → Warning logged, not registered; `IsApplicable()` true → registered in registry; extension with both tools and provider → both registered; extension with neither → error
