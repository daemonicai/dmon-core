## Context

Provider resolution today has two disconnected halves:

- **Instances** come only from `config.yaml`'s `providers:` map. `ProviderConfigLoader.Load()` parses it into `IReadOnlyList<ProviderConfig>`, registered in DI as `IEnumerable<ProviderConfig>` (`DaemonServiceExtensions.AddDmonCore`). This single enumerable is injected into **both** `ProviderRegistry` (the provider list) **and** `CredentialResolver` (keyed by `ProviderConfig.Name` for env-var/file key lookup).
- **Factories** come from `Use<Provider>()` verbs in `Dmon.cs`, DI-discovered as `IEnumerable<IProviderFactory>` (ADR-022/023). A cloud verb registers only the factory; it adds no `ProviderConfig`.

Consequences: a config entry whose adapter has no wired factory is skipped with a warning in the `ProviderRegistry` ctor (per ADR-023 D7 — note the standing spec still wrongly says "throws"); and a wired factory with no config entry is invisible. The default project ships six config providers and zero wired factories, so every startup logs six warnings and no provider is actually usable. `ProviderRegistry.EnsureProviderConfigured` throws `"At least one provider must be configured."` if the list is empty, so the `providers:` map cannot simply be deleted.

Local `IProviderExtension` providers already close this gap on their side: `ProviderRegistry.RegisterExtensionAsync` synthesizes a `ProviderConfig` from the extension's factory at registration time. Only cloud `IProviderFactory` verbs lack the equivalent.

## Goals / Non-Goals

**Goals:**
- A wired cloud `Use<Provider>()` verb alone makes its provider exist and be usable (key resolved, listable, selectable) with no `config.yaml` entry.
- `config.yaml`'s `providers:` map becomes optional; when present, an entry overrides/augments the synthesized default by name (custom `baseUrl`, `defaultModelId`, `auth`, or extra named instances on the same adapter).
- Removing the `providers:` map from `.dmon/config.yaml` leaves a working agent driven entirely by the verbs in `Dmon.cs`.
- Reconcile the standing specs with the shipped warn-and-skip behavior for unknown-adapter config entries.

**Non-Goals:**
- No change to local `IProviderExtension` synthesis (already correct), to the wizard/setup flow, to credential resolution order (ADR-005), or to provider/model switching semantics.
- No new ADR — this implements the stated intent of ADR-022/023.
- No back-compat shim (no production deployments).

## Decisions

### Decision 1: Synthesize defaults at the shared DI seam, not inside ProviderRegistry

Compose the merged provider list where `IEnumerable<ProviderConfig>` is registered in `DaemonServiceExtensions.AddDmonCore`, using the DI-resolved `IEnumerable<IProviderFactory>`:

```
config-derived (ProviderConfigLoader.Load())  ++  synthesized defaults for each
   registered factory whose AdapterName is NOT already the Adapter of a config entry
```

**Why here and not in the registry ctor:** `CredentialResolver` is built from the *same* enumerable and looks providers up by `Name`. If synthesis happened only inside `ProviderRegistry`, the resolver would have no entry for a verb-only provider and would return `null` for its API key. Composing at the shared seam means registry, resolver, and every other consumer see one consistent list. A small helper (`ProviderConfigComposer.Compose(fromConfig, factories)`) owns the merge so it is unit-testable in isolation.

*Alternative considered — verb registers a `ProviderConfig` marker into `Services`:* rejected as more plumbing (new marker type + merge-on-resolve) and asymmetric with the existing local-extension path, which already centralizes synthesis.

### Decision 2: Synthesized default shape

For a factory `f` with no representing config entry:
- `Name` = `Adapter` = `f.AdapterName`
- `DefaultModelId` = `f.DefaultModelId`
- `Auth` = `{ Type = "envVar", EnvVar = f.DefaultEnvVar }` when `f.DefaultEnvVar` is non-empty, else `{ Type = "none" }`
- `BaseUrl` = `null` (cloud SDK default). Local providers needing a `baseUrl` flow through the `IProviderExtension` path, not this synthesis.

"Represented" = case-insensitive match of `f.AdapterName` against the `Adapter` of any config-derived entry. If a config entry already names that adapter (even under a custom `Name`), no default is synthesized for it — config wins, and the user can add an explicit `name: adapter` entry if they want both.

### Decision 3: Ordering and the default provider

The merged list is `config-derived first` (preserving today's index-0 = first config entry), then `synthesized defaults in factory registration order` (= verb order in `Dmon.cs`). With the `providers:` map removed, the default provider (index 0, restored selection aside) is therefore the **first `Use<Provider>()` verb** in `Dmon.cs`. This is deterministic and matches author intent.

### Decision 4: Keep the warn-and-skip; relax only the empty case

`ProviderRegistry`'s existing "config entry for an unregistered adapter → warn and skip" stays (still useful diagnostics). `EnsureProviderConfigured` still throws on a genuinely empty list — but "empty" now means *no config entries AND no wired factories*, a real misconfiguration. The standing specs are corrected so the normative behavior is warn-and-skip (not throw) for unknown adapters.

## Risks / Trade-offs

- [A config entry under a custom name suppresses the synthesized default for that adapter] → Documented in Decision 2; users wanting both add an explicit `anthropic: { adapter: anthropic }` entry. Low impact (uncommon).
- [Spec reconciliation is technically a normative change to shipped behavior] → It only aligns the spec with code that already shipped under ADR-023 D7; no runtime behavior changes for that path.
- [Removing `providers:` from the default `config.yaml` is user-visible] → Intended; the new `Dmon.cs` is the documented place to declare providers, and `config.yaml` overrides still work for customization.

## Migration Plan

1. Land `ProviderConfigComposer` + DI wiring + tests (no behavior change until verbs are wired).
2. Update root `Dmon.cs` to wire providers via verbs and remove the `providers:` map from `.dmon/config.yaml`.
3. Rollback = revert; no persisted state or wire-protocol change is involved.

## Open Questions

None.
