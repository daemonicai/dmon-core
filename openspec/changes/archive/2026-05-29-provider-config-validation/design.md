## Context

`ProviderConfigLoader.Load()` iterates `_configuration.GetSection("providers").GetChildren()` and, for each child, takes `providerSection.Key` as the provider `Name`. The only validation is a non-empty `adapter`. .NET's configuration model represents both YAML maps and YAML sequences as keyed children — a sequence yields children keyed `0,1,2,…`. So a sequence-form `providers` block loads "successfully" with numeric names, and downstream the `/model` picker shows `0..5`. No layer in the chain (loader → `ProviderRegistry` → `ModelListHandler` → host picker) treats numeric names as wrong.

The canonical schema is the map-keyed form written by `BootstrapService` and documented in `docs/configuration.md`. This change makes the loader enforce it.

## Goals / Non-Goals

**Goals:**
- A malformed `providers` block fails loudly at load with a message that names the offending entry and shows the expected schema.
- Specifically reject sequence-form (numeric-keyed) and empty/whitespace-keyed entries.
- Extend `ProviderConfigLoaderTests` to cover the sequence-form mistake, an empty/whitespace key, and the `auth`-omitted default (the existing suite already covers happy path, multiple providers, `baseUrl`, empty section, and missing `adapter`).

**Non-Goals:**
- No new/alternative `providers` schema. The map-keyed form stays canonical.
- No general unknown-field linting (e.g. catching `model:` typo'd for `defaultModelId:`). Out of scope; a sequence-form block is already rejected wholesale, and field-level linting is a larger, separate concern.
- No change to `ProviderRegistry`, `ModelListHandler`, or the RPC/protocol surface.

## Decisions

**Detect sequence form via purely-numeric keys.** When `providers` is a YAML sequence, the binder keys children `0,1,2,…`. The loader will reject any provider whose section key matches `^\d+$`. This is the precise, deterministic signature of the sequence-form mistake.
- *Rationale:* it is the binder's observable contract for sequences and needs no YAML-layer access (the loader only sees `IConfiguration`).
- *Alternative considered — inspect raw YAML to distinguish map vs sequence:* rejected; the loader is decoupled from the config source (file, env, in-memory) and only consumes `IConfiguration`. Reaching back to the YAML would couple it to one provider.
- *Trade-off:* a provider literally named `"0"` becomes illegal. Acceptable — a numeric provider name is meaningless for `/model` selection, and the spec documents the restriction.

**Throw `InvalidOperationException` with an actionable message.** Consistent with the existing missing-`adapter` throw and with `ProviderRegistry`'s unknown-adapter throw. The message names the offending key and embeds the canonical map-keyed snippet so the fix is obvious from the error alone.
- *Rationale:* startup-time fail-fast matches the established "validated at construction" posture of the registry capability.

**Validation order per entry:** reject empty/whitespace key → reject numeric key → require `adapter`. This surfaces the most structural problem first.

## Risks / Trade-offs

- [A user with a working numeric-named provider breaks on upgrade] → No such configuration is usable today (numeric names come only from the sequence-form mistake, which never worked), so there is no valid case to preserve.
- [Over-eager rejection of legitimate keys] → Only `^\d+$` keys and empty keys are rejected; ordinary provider names (`anthropic`, `llama.cpp`, `oMLX`) are unaffected and covered by the happy-path test.

## Migration Plan

No data migration. The already-corrected `.dmon/config.yaml` (map form) is the reference. Users on the broken sequence form will see the new actionable error on next startup and edit their config to the map form — exactly the outcome this change exists to produce.
