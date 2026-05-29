## Why

`ProviderConfigLoader` derives each provider's name from its `IConfiguration` section **key** and validates nothing beyond the presence of `adapter`. When a user writes the `providers` block as a YAML **sequence** instead of a name-keyed **map**, .NET's configuration binder keys the entries `0,1,2,…`. The loader silently accepts this: providers are named `"0".."5"`, `defaultModelId` is dropped (a sequence author naturally writes `model:`), and `auth` falls back to `none`. The `/model` picker then lists providers as `0,1,2,3,4,5` with no error anywhere in the chain. The failure is invisible until a human notices the picker is wrong.

## What Changes

- `ProviderConfigLoader` validates the shape of the `providers` section and **throws a clear, actionable error** when it is malformed, rather than producing index-named providers:
  - Detect numeric/sequence-form keys (`0`, `1`, …) and reject with a message that names the offending key and states the expected map-keyed schema.
  - Reject a provider whose key is empty/whitespace.
  - Keep the existing missing-`adapter` rejection; ensure its message also points at the expected schema.
- The error message is **actionable**: it names the failing entry and shows the canonical `providers:` map form (the same shape `BootstrapService` writes and `docs/configuration.md` documents).
- Extend `ProviderConfigLoaderTests` with the cases the existing suite lacks: the sequence-form mistake (numeric-keyed entries throw), an empty/whitespace key (throws), and the `auth` default (`type: none` when the `auth` block is omitted entirely). The current suite already covers the happy path, multiple providers, `baseUrl`, empty section, and missing `adapter`.
- **No new schema.** The canonical map-keyed-by-name form is retained; a competing sequence schema is explicitly *not* introduced.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `provider-registry`: the config-driven loading requirement gains validation behaviour — a malformed `providers` block (sequence/numeric-keyed or empty-keyed entries) SHALL fail loudly at load with an actionable error instead of yielding index-named providers.

## Impact

- **Code:** `src/Dmon.Core/Providers/ProviderConfigLoader.cs` (validation logic). New cases in the existing `test/Dmon.Core.Tests/Providers/ProviderConfigLoaderTests.cs`.
- **Behaviour:** a previously-silent misconfiguration now aborts startup with a descriptive error. This is the intended correction — an index-named provider set was never usable.
- **Docs/specs:** `openspec/specs/provider-registry/spec.md` delta. No ADR changes (ADR-005 unaffected; canonical schema unchanged).
- **No API/protocol changes.**
