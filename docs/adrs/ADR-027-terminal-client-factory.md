# ADR-027: Terminal Client Factory and Ability Registry as Core Composition Seams

**Date:** 2026-06-18
**Status:** Accepted
**Amends:** ADR-019 (the `DmonHostBuilder.Build()` terminal-client selection); ADR-022 (adds two facetless host-level seams to the composition-root surface)
**Builds on:** ADR-023 (vendor-SDK-free engine; no product taxonomy in core), ADR-026 (the precise meaning of "middleware")

## Context

`DmonHostBuilder.Build()` (ADR-019) resolves exactly one terminal `IChatClient`: the active provider selected from the provider registry. The whole pipeline — permission gate, retry, the middleware fold — is built over that single client.

A class of agent does not fit this shape. The Daemon personal assistant (the `daemon-app` change) needs a terminal client that holds **three** backends — a fast always-warm local model, a larger local reasoner, and a gated cloud model — and selects among them per turn, gating each turn's tool manifest by a privacy scope. There is no seam for "be the terminal client" today short of abusing the provider registry to hold named clients.

The tempting framing is "the router is middleware." It is not. ADR-026 fixed the meaning of middleware in this repo to exactly the ADR-023 chat-pipeline role: an `IDmonMiddleware.Wrap(IChatClient inner)` participant that wraps **one** inner client (`core/Dmon.Abstractions/Extensions/IDmonMiddleware.cs`). A router holds several independent backends and selects among them; it does not wrap a single inner client, so it *is* the terminal client, not a wrapper. Filing it as middleware — a `Dmon.Middleware.Triage` package, verbs on `IMiddlewareRegistration` — would contradict ADR-026 D4 (which keeps `middleware/` denoting exactly that role, with no members until a real `IDmonMiddleware` ships) and dilute the meaning of the bucket all over again.

Two needs fall out, and they are at different altitudes:

1. **A general seam** — "let a composition root supply its own terminal `IChatClient`." This is engine infrastructure: routers, A/B clients, scoped sub-agent fan-out clients all want it. It belongs in `core/`.
2. **Application policy** — the Daemon's specific three-backend router, its scope vocabulary, its classifier prompt, its dispatch switch. This is not reusable engine infrastructure; it is one agent's product, and it belongs with that agent.

Separately, an agent that routes by privacy scope needs to hand a turn only the tools allowed for that scope — enforced in C# by what is **absent** from the manifest, not by model behaviour. That is a tool-manifest builder partitioned by a scope label, distinct from the global `IToolExtension` pipeline. The *mechanism* is general; the *vocabulary* ("personal"/"world") is the Daemon's product taxonomy and must not leak into a vendor-SDK-free engine (ADR-023).

## Decision

1. **`ITerminalClientFactory` is a core seam.** A public interface in `Dmon.Abstractions` declaring `IChatClient Create(IServiceProvider services)`. When exactly one is registered in DI, `DmonHostBuilder.Build()` — after provider/middleware DI-discovery — resolves it and uses its `Create(host.Services)` output as the terminal `IChatClient`, in place of the provider-registry active provider. When none is registered, `Build()` uses the provider-registry active provider exactly as before; the no-factory path is behaviourally unchanged. This **amends ADR-019**'s single-source terminal-client selection by adding one precedence rule, nothing more.

   `Create` is synchronous and does no I/O: the factory resolves already-constructed backends from the same `IServiceProvider` and composes them. **Exactly one** `ITerminalClientFactory` may be registered — competing factories are out of scope; one router per agent is the right constraint now.

2. **`IAbilityProvider` + `AbilityRegistry` are core seams, keyed on an opaque string scope.** `IAbilityProvider` (in `Dmon.Abstractions`) exposes `string Scope { get; }` and `IEnumerable<AITool> Tools { get; }`. `AbilityRegistry` (in `Dmon.Core`, a singleton registered by `AddDmonCore()`) discovers all registered providers by build-time DI enumeration and, on each `ForScope(scope)` call, returns exactly the tools whose provider declared that scope (`OrdinalIgnoreCase`), built per-call and never cached. `AddAbilities<T>()` is a verb on `IToolRegistration` registering `T` as an `IAbilityProvider` singleton — same pattern as `AddToolExtension<T>()`.

3. **Core defines no scope vocabulary.** `Scope` is a `string`, not an enum. Encoding `Personal`/`World` as a core enum would put a Daemon product taxonomy into a vendor-SDK-free engine, contradicting ADR-023. A `tools/` package (e.g. `Dmon.Tools.Calendar`) declares the literal `"personal"` with no dependency on the Daemon, and the consuming agent agrees on the vocabulary by convention. The trade-off — losing a shared enum's compile-time guarantee — is accepted: the alternative forces either a product taxonomy into core or a Daemon dependency onto `tools/` packages. Consumers wanting type safety may define their own constants over the string.

4. **`AbilityRegistry` is orthogonal to `IToolExtension`.** A tool registered via `AddToolExtension<T>()` is in the global pipeline and **not** in the ability registry; an ability registered via `AddAbilities<T>()` is surfaced per-turn by scope and does **not** enter the global pipeline. A turn's manifest is built from the registry, never the global pipeline.

5. **Routing policy is application code, not middleware, and not a first-party engine package.** The Daemon's `TriageRouter`, its `Tier`/`RouteDecision`/`TriageOptions` types, its classifier prompt, its dispatch switch, and its `UseTriage`/`AddReasoner`/`AddEgress` verbs live in `daemon/Daemon.Routing` (the `daemon-app` change), not in `middleware/` and not on the protocol-lockstep first-party release train (ADR-024). The `middleware/` bucket **stays empty** until a real `IDmonMiddleware` ships — ADR-026 D4 is upheld, not eroded. Egress is supplied as an explicit, provider-agnostic `AddEgress(IChatClient)`; the router references no provider package.

## Consequences

- **The core stays an engine.** Both new contracts are mechanism, not policy: no scope names, no router, no backend count baked in. Any future multi-backend or scope-gated agent uses the same two seams without touching core.
- **`middleware/` keeps its meaning.** A multi-backend terminal client is no longer mistaken for an `IDmonMiddleware`. The bucket's first physical member will still be an actual pipeline interceptor (ADR-026 D4).
- **One precedence rule added to `Build()`.** The change is additive: present-factory wins, absent-factory is unchanged. Non-routing composition roots are entirely unaffected — verified by a no-factory test asserting the provider-registry path.
- **`ITerminalClientFactory` is narrow by design.** Exactly-one registration. If competing factories are ever needed (e.g. two routers composed), this ADR is revisited; the constraint is deliberate, not an oversight.
- **String scope is stringly-typed.** Mitigated by `OrdinalIgnoreCase` comparison and a documented convention. A casing or spelling mismatch silently drops a tool from a manifest rather than leaking one — the fail-safe direction for a privacy gate, but still a class of bug consumers must test for.
- **No release-matrix change.** Both contracts ship inside the existing `Dmon.Abstractions`/`Dmon.Core` packages; no new first-party package, so ADR-024's per-package trigger model is untouched.

## Alternatives

- **Register the router as an `IDmonMiddleware` that wraps the active provider.** Rejected: middleware wraps exactly one inner client; a router holds several backends and selects among them. Conflating the two contradicts ADR-026's fixed meaning of middleware and would require the router to fake an `inner` it does not use.
- **Hold the three backends as named entries in the provider registry.** Rejected: the provider registry models *one active provider* with a model list; overloading it to carry a classifier + reasoner + egress trio distorts its contract and still leaves `Build()` picking one "active" client. A dedicated factory seam is honest about the shape.
- **A core `Scope` enum (`Personal`/`World`).** Rejected per Decision 3 — it puts a product taxonomy into a vendor-SDK-free engine (ADR-023) and forces every `tools/` ability author to reference the enum's home.
- **Ship the seams *and* the router together as one `middleware/Dmon.Middleware.Triage` package (the original `triage-middleware` proposal).** Rejected: it bundles a general seam with one application's policy, mislabels the policy as middleware, and puts Daemon-specific code on the protocol-lockstep release train. Split into general seams (here, in `core/`) and application policy (`daemon/Daemon.Routing`).

## Open Questions

- **A. Async terminal-client construction.** `Create` is synchronous on the assumption that backends are pre-constructed and composition is allocation-only. If a future factory needs async warm-up (e.g. probing a local model is resident before deciding the pipeline), an async overload or a warm-up hook is added then. Out of scope here.
- **B. Multiple terminal-client factories.** Exactly-one today (Consequences). The shape of a "compose two factories" story (precedence? chaining?) is deferred until a second real consumer exists.

## Relationship to other ADRs

- **ADR-019** — amended: `Build()`'s terminal-client selection gains the `ITerminalClientFactory` precedence rule. The hosting surface, wire contract, and `RunAsync` loop are otherwise unchanged.
- **ADR-022** — extended: `ITerminalClientFactory` and `IAbilityProvider` join the composition-root surface; `AddAbilities<T>()` follows the established self-typed verb grammar on `IToolRegistration`. `UseTriage`/`AddReasoner`/`AddEgress` are host-level verbs (like `UseMemory`) but ship from `daemon/`, not core.
- **ADR-023** — honoured: core takes no vendor SDK and no product taxonomy; the router and its egress live in `daemon/` as application code, referencing no provider package.
- **ADR-026** — upheld: "middleware" stays the `IDmonMiddleware` role and `middleware/` stays empty. This ADR is the same category discipline applied to a *terminal client* rather than to *memory*.
- **ADR-024** — unaffected: no new first-party package; both seams ship in existing core packages.
