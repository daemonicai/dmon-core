## Why

Per ADR-022/023 the composition root (`Dmon.cs`) is meant to be the source of truth for which providers an agent has — "nothing baked into `dmoncore`", and the stock `Dmon.cs` "lists its providers explicitly" via `Use<Provider>()` verbs. But today a cloud `Use<Provider>()` verb only registers an `IProviderFactory` (an adapter); it contributes **zero** `ProviderConfig` instances. The provider *list* is sourced solely from the `providers:` map in `config.yaml` (via `ProviderConfigLoader`). The two halves are therefore mismatched: wiring `UseAnthropic()` without a matching `config.yaml` entry yields a provider the registry never lists, and listing a provider in `config.yaml` without wiring its factory logs `"Provider 'X' … no factory is registered"` at every startup (which is what the default project currently does for all six providers). Removing the `providers:` map outright is impossible — `ProviderRegistry` throws `"At least one provider must be configured."`

## What Changes

- A cloud `Use<Provider>()` verb's registered `IProviderFactory` SHALL yield a **default `ProviderConfig`** so that wiring the verb alone makes the provider usable: `Name`/`Adapter` = `IProviderFactory.AdapterName`, `DefaultModelId` = `IProviderFactory.DefaultModelId`, and `Auth` = `{ type: envVar, envVar: DefaultEnvVar }` when the factory declares a `DefaultEnvVar`, else `{ type: none }`. (Local `IProviderExtension` providers already do this via `RegisterExtensionAsync`; this brings cloud factories to parity.)
- `config.yaml`'s `providers:` map becomes **OPTIONAL**. A `config.yaml` entry **overrides/augments** a synthesized default by provider name (custom `baseUrl`, `defaultModelId`, `auth`, or an additional named instance on the same adapter) but is no longer **required** to make a wired provider exist.
- `ProviderRegistry` SHALL synthesize defaults for registered factories whose adapter is **not already represented** by a `config.yaml` entry, and SHALL **not** throw when `config.yaml` has no `providers:` map as long as at least one provider is wired via a verb. It SHALL still throw when there are genuinely zero providers (no config entries **and** no wired factories).
- **BREAKING (spec reconciliation):** the standing "unknown adapter at startup → throws `InvalidOperationException`" scenario is corrected to match the shipped ADR-023 D7 behavior — a `config.yaml` entry whose adapter has no registered factory is **skipped with a warning**, not fatal.
- The root `Dmon.cs` is updated to wire its providers explicitly via verbs, and the `providers:` map is removed from `.dmon/config.yaml` (proving the new path end to end).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `provider-registry`: provider-list sourcing changes — the registry derives providers from wired factories (synthesized defaults) merged with optional `config.yaml` overrides; empty-`providers:` no longer fatal when factories are wired; unknown-adapter config entries are skipped-with-warning, not fatal.
- `provider-factories`: a cloud `Use<Provider>()` verb now contributes a default `ProviderConfig` derived from the factory (not just a factory registration), reaching parity with local `IProviderExtension` synthesis.

## Impact

- **Code:** `core/Dmon.Core/Providers/ProviderRegistry.cs` (default synthesis + merge + no-throw-when-wired), `core/Dmon.Core/Providers/ProviderConfigLoader.cs` (tolerate absent `providers:`), possibly `core/Dmon.Core/DaemonServiceExtensions.cs` (provider-list assembly).
- **Composition root / config:** root `Dmon.cs` (explicit `Use<Provider>()` list), `.dmon/config.yaml` (`providers:` map removed).
- **Tests:** `test/Dmon.Core.Tests/Providers/ProviderRegistryTests.cs` and provider-related fixtures.
- **ADRs:** consistent with ADR-022/023; no new ADR required.
- **No production deployments** — clean break, no back-compat shim needed.
