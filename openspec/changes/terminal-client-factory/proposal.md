## Why

A composition root currently gets exactly one terminal `IChatClient`: the active provider selected from the provider registry in `DmonHostBuilder.Build()`. Some agents need to *be* a custom terminal client instead — one that holds several backends and selects among them per turn (the Daemon's triage router is the first such consumer, but the need is general: A/B clients, scoped sub-agent routers, fan-out clients). There is no seam for that today short of abusing the provider registry.

Separately, an agent that routes by privacy scope needs a way to hand a turn only the tools allowed for that scope, enforced in C# by what is *absent* from the manifest rather than by model behaviour. That requires a tool-manifest builder that partitions tools by a scope label, distinct from the global `IToolExtension` pipeline.

Both are general composition seams, not application policy. This change adds them to `core/` so that the Daemon (Phase 3) — and any future multi-backend or scope-gated agent — can be assembled from a `.cs` composition root without modifying the core. It deliberately does **not** add the triage router itself; that is Daemon-specific policy and lands with `daemon-app`.

## What Changes

- `ITerminalClientFactory` in `core/Dmon.Abstractions`: a single-implementation interface whose presence in DI signals that `DmonHostBuilder.Build()` should use its output as the terminal `IChatClient` in place of the provider-registry active provider.
- `DmonHostBuilder.Build()` resolves `ITerminalClientFactory` after provider/middleware DI-discovery; if present, uses it; otherwise the existing provider-registry path is unchanged.
- `IAbilityProvider` in `core/Dmon.Abstractions`: declares a string `Scope` label and a set of `AITool`s. The scope vocabulary (e.g. `"personal"`/`"world"`) is a convention agreed between ability authors and the consuming agent — core defines no taxonomy.
- `AbilityRegistry` in `core/Dmon.Core`: the C# scope gate. `ForScope(scope)` returns exactly the tools whose `IAbilityProvider` declared that scope, built per-call, never cached across turns. Registered as a singleton in `AddDmonCore()`.
- `AddAbilities<T>()` verb on `IToolRegistration`: registers `T` as an `IAbilityProvider` singleton via DI-discovery, same pattern as `AddToolExtension<T>()`.

## Capabilities

### New Capabilities

- **ability-registry**: Scope-aware tool-manifest builder. `AbilityRegistry.ForScope(scope)` returns only the tools whose provider declared that scope label; tools from other scopes are never included. `IAbilityProvider`s are discovered by build-time DI enumeration. Independent of the global `IToolExtension` pipeline.

### Modified Capabilities

- **composition-root-hosting**: `DmonHostBuilder.Build()` gains an `ITerminalClientFactory` resolution step — when one is registered, its output replaces the provider-registry active provider as the terminal `IChatClient`. The no-factory path is unchanged.

### Removed Capabilities

_(none)_
