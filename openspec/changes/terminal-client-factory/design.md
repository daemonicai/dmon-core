## Context

`DmonHostBuilder.Build()` resolves one active terminal `IChatClient` from the provider registry. The Daemon agent (Phase 3) needs to replace that single client with a router holding three backends, and needs to gate each turn's tool manifest by a privacy scope. Neither capability should be expressed as Daemon-specific code in the core, nor should the router be smuggled in as "middleware": this repo defines middleware precisely as `IDmonMiddleware.Wrap(IChatClient inner)` (the `extension-middleware` spec), and a multi-backend terminal client does not wrap a single inner client.

This change therefore adds two **general** composition seams to `core/` and nothing application-specific. The triage router, its scope taxonomy, and its builder verbs are Daemon policy and land with `daemon-app`.

## Goals / Non-Goals

**Goals:**
- `ITerminalClientFactory` (contract in `Dmon.Abstractions`) and a `Build()` hook that uses it in place of the provider-registry active provider when present.
- `IAbilityProvider` (contract in `Dmon.Abstractions`) and `AbilityRegistry` (impl in `Dmon.Core`) as the C# scope gate, keyed on an opaque string scope label.
- `AddAbilities<T>()` verb on `IToolRegistration`.
- Unit tests for the `Build()` hook (factory present / absent) and all `ForScope` partition invariants.

**Non-Goals:**
- `TriageRouter`, `RouteDecision`, `Tier`, `TriageOptions`, classify/dispatch logic, the misclassification metric, and the `UseTriage`/`AddReasoner`/`AddEgress` verbs — all Daemon policy (`daemon-app`).
- A concrete scope vocabulary. Core defines no `Personal`/`World` constants; the vocabulary is a convention between ability authors and the consuming agent.
- Any concrete ability implementation (calendar/email/memory — Phase 2 and beyond).

## Decisions

### D1: TriageRouter-style clients hook into Build() via ITerminalClientFactory

`Build()` currently selects the terminal `IChatClient` from the provider registry. A multi-backend client needs to *be* the terminal client, not be selected as one provider. Rather than abusing the provider registry to hold named clients, we introduce `ITerminalClientFactory` — a single-implementation interface whose presence in DI signals that a custom terminal client should replace the registry selection.

`Build()` resolves `host.Services.GetService<ITerminalClientFactory>()` after provider/middleware DI-discovery; if present, calls `Create(host.Services)` for the terminal client; otherwise falls back to the existing provider-registry path. The existing single-provider path is untouched for all non-factory composition roots.

`ITerminalClientFactory.Create` is synchronous (`IChatClient Create(IServiceProvider services)`): the factory resolves already-constructed backends from DI and composes them; it does no I/O. Exactly one `ITerminalClientFactory` may be registered — if a future scenario needs competing factories this is revisited, but exactly-one is the right constraint now.

**Alternative considered:** register the router as an `IDmonMiddleware` that wraps the active provider. Rejected — middleware (`extension-middleware`) wraps exactly one inner client; a router holds several independent backends and selects among them, so it is the terminal client, not a wrapper. Conflating the two would contradict the accepted middleware contract.

### D2: AbilityRegistry is orthogonal to IToolRegistration

`IToolExtension` registers tools globally into the main pipeline. `IAbilityProvider` declares tools that an agent surfaces *per turn* by scope, never in the global pipeline. These serve different purposes and must not collide:

- A tool registered via `AddToolExtension<T>()` is **not** in the ability registry.
- An ability registered via `AddAbilities<T>()` does **not** appear in the global tool pipeline.

`AddAbilities<T>()` is a verb on `IToolRegistration` (abilities are tool-shaped; the facet split puts tool verbs there) and registers `T` as an `IAbilityProvider` singleton. `AbilityRegistry` enumerates all registered `IAbilityProvider`s (build-time DI enumeration) and partitions their tools by scope on each `ForScope` call.

### D3: Scope is an opaque string label, not a core enum

`IAbilityProvider.Scope` is a `string`. Core ships the partition *mechanism* and no vocabulary: encoding `Personal`/`World` as a core enum would put a Daemon product taxonomy into a vendor-SDK-free engine (ADR-023). A `tools/` package such as `Dmon.Tools.Calendar` can declare `Scope => "personal"` with no dependency on the Daemon, and the Daemon agrees on `"personal"`/`"world"` by convention. Scope comparison in `ForScope` is `OrdinalIgnoreCase` so casing mismatches do not silently drop a tool from (or leak a tool into) a manifest.

**Trade-off:** loses the compile-time guarantee a shared enum would give. Accepted: the alternative forces either a Daemon taxonomy into core or a Daemon dependency onto `tools/` packages. Consumers that want type safety can define their own constants over the string.

### D4: AbilityRegistry is always registered; manifests are built per call

`AbilityRegistry` is registered as a singleton in `AddDmonCore()` regardless of whether any `IAbilityProvider` is present (it resolves an empty `IEnumerable<IAbilityProvider>` harmlessly when none are). `ForScope` re-partitions on every call rather than caching, so a consuming router gets a fresh manifest per turn. This is negligible for the expected tool counts (< 20) and could be memoised if it ever shows in profiling.

## Risks / Trade-offs

- **`ITerminalClientFactory` is a narrow extension point**: exactly-one registration. Adequate for the foreseeable consumers (one router per agent); revisit if competing factories are ever needed.
- **String scope is stringly-typed**: see D3. Mitigated by `OrdinalIgnoreCase` comparison and a documented convention; consumers may wrap it in their own constants.

## Open Questions

_(none — the egress/Gemini wiring question that previously lived here, OQ-B, is resolved in `daemon-app` by an explicit `AddEgress(IChatClient)` verb rather than a provider-name lookup, and is out of scope for these core seams.)_
