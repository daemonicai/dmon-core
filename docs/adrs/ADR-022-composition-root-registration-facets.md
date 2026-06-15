# ADR-022: Composition-Root Registration Facets, Sub-Agent Isolation, and Agent-as-Composition-Root

**Date:** 2026-06-15
**Status:** Accepted
**Amends:** ADR-019 (the hosting surface of Decision 1 — `CreateBuilder → builder → Build().RunAsync()` — is restructured into registration facets, DI-discovery, and an open verb grammar); ADR-002 (the contract is renamed `IDmonExtension` → `IToolExtension`; shape unchanged); ADR-010 (supplies the deferred mechanism and **reverses its "no new contract type" consequence**, while **retaining its scope boundary** — single-turn, independent, not orchestration); ADR-006/ADR-021 (provider composition is an author-time `compose`-tier surface, not a runtime gate).
**Supersedes:** ADR-013 **in full** (the agent-profile-as-named-bundle concept is dissolved; its capabilities — system prompt, permission mode, session assets — become builder verbs, and per-session selection becomes choice of `.cs` composition root); ADR-020's **`.md`-persona + bundle framing** (an agent *is* its `.cs`; retains ADR-020's per-session selection, `.dmon/agents/` location, and gateway-workspace-root resolution — ADR-019 Open Question E — now keyed to `.cs` roots rather than `.md`+`.cs` pairs).
**Builds on:** ADR-007 (`IProviderExtension` lifecycle, `IProviderFactory.CreateAsync`), ADR-019 (composition root), ADR-001 (`IChatClient` / M.E.AI).

## Context

ADR-019 made `dmoncore` a library whose entry point is a user-authored `Dmon.cs` composition root, and exposed a fluent surface: `DmonHost.CreateBuilder(args)` → `DmonHostBuilder` → `.Build().RunAsync()`. That surface is a sealed concrete class in `Dmon.Core` with a fixed, ad-hoc method set (`WithModel`, `AddExtension`, `AddMiddleware`, `WithProfile`, `WithPermissionMode`, `WithStdio`, `WithoutTelemetry`, `ConfigureConfiguration`). It works, but it cannot grow without editing core, and the composition root is meant to become a **headline feature** — the place a user assembles an agent from packages with one-liners like `.UseLlamaCpp("unsloth/gemma4")` or `.AddAgentWebSearch("gemini-3.5-flash-lite")`. Several things block that today:

- **The builder is closed.** A third-party provider package cannot contribute `.UseLlamaCpp(...)` — every verb must be a method on the sealed `DmonHostBuilder` in `Dmon.Core`. Extension authors get no fluent surface of their own.
- **There is no provider verb at all.** `DmonHostBuilder` has `AddExtension` (tools) and `AddMiddleware`, but **nothing** for providers. `IProviderExtension` (ADR-007) is **orphaned** after ADR-019 deleted the dynamic loader that was its only route into the host — a contract with no composition entry point.
- **Providers are baked, asymmetrically.** Cloud providers register directly as `IProviderFactory` DI singletons inside `AddDmonProviders()`; local providers are `IProviderExtension` (lifecycle-gated). Neither is composed by the user.
- **Registration is post-build and manual.** `AddExtension`/`AddMiddleware` accumulate into lists the builder replays into `IToolRegistry`/`IMiddlewareRegistry` *after* `host.Build()`. An extension method cannot hook a private post-build loop.
- **Tool extensions cannot take dependencies.** `AddExtension<T>` requires `where T : new()`. A sub-agent tool (ADR-010) needs services injected.
- **No way to build a non-active client.** `IProviderRegistry` is stateful and singular (`GetCurrentAsync`, `SetModel`); a sub-agent tool that runs a *different* model than the root (e.g. web search on a cheap flash model while the root runs GPT) has nowhere to obtain that client. ADR-010 sanctioned the pattern but left the mechanism to "resolve `IEnumerable<IProviderFactory>` and call `CreateAsync` yourself," which is boilerplate and easy to get wrong (D3's independence rule is unenforced).
- **The name `IDmonExtension` is overloaded** — it means both the umbrella ("things you compose") and specifically the *tools bundle*, which sits awkwardly beside a growing `UseProvider`/`AddMiddleware` vocabulary.

This ADR turns the composition root into an **open, faceted, DI-backed** surface, and in doing so supplies ADR-010's missing mechanism through the *same* abstraction used to register the root's provider.

## Decision

1. **Registration is split into role-specific facet interfaces, each living with its contracts.**

   | Facet | Verbs it carries | Extended by |
   |---|---|---|
   | `IProviderRegistration` | `UseOpenAI`, `UseLlamaCpp`, `UseModel`, … | provider packages |
   | `IToolRegistration` | `AddToolExtension`, `AddAgentWebSearch`, … | tool packages |
   | `IMiddlewareRegistration` | `AddMiddleware`, … | middleware packages |

   **All three facets — and every other author-facing contract — live in a single `Dmon.Abstractions` package; `Dmon.Extensions` is deleted (Decision 12).** Every extension author references exactly one contract package regardless of kind. Because `Dmon.Abstractions` carries only `Microsoft.Extensions.AI` + `Dmon.Protocol` (the vendor SDKs all sit in the *implementation* packages — Decision 7's granular split), the only cost of a single contract package is interface *visibility*, not deployment weight. A sub-agent tool author (who needs both `IToolRegistration` and `IProviderRegistration`, per Decision 6) therefore references one package, not two.

2. **`IDmonHostBuilder` aggregates the facets and is a thin DI wrapper.** It is declared in `Dmon.Abstractions` alongside the facets and exposes the escape hatch plus host-level options:

   ```csharp
   public interface IDmonHostBuilder
       : IProviderRegistration, IToolRegistration, IMiddlewareRegistration
   {
       IServiceCollection Services { get; }
       IConfigurationManager Configuration { get; }
   }
   ```

   Host-level, non-pluggable verbs (`WithStdio`, `WithoutTelemetry`, `WithProfile`, `WithPermissionMode`, `UseSystemPrompt`, `AppendToSystemPrompt`, `UseMemory<TShort,TLong>`) are extension methods on `IDmonHostBuilder`. The concrete `DmonHostBuilder` and `DmonHost` stay in `Dmon.Core`.

3. **Every verb is an extension method over `Services`/`Configuration`; blessed verbs ship in the default-imported namespace.** There is no "members vs. extension methods" choice — *all* verbs are extension methods (the ASP.NET Core `WebApplicationBuilder` pattern). A verb is "built-in" iff it ships in a namespace `Dmon.cs` already imports; a third-party verb is one `using` away. Under the covers a verb only ever calls `Services.Add…` / `Configuration[…]`:

   ```csharp
   // Dmon.Abstractions
   public static T UseLlamaCpp<T>(this T r, string model) where T : IProviderRegistration
       => r.AddProvider<LlamaCppProvider>().UseModel($"llamacpp/{model}");
   ```

4. **Verbs use a self-type generic so flat and faceted chaining both work.** Because `DmonHostBuilder` implements all three facets, a facet verb called on the builder returns the builder type and chaining continues across facets; the same verb called on a bare facet returns that facet. The grouping properties (`builder.Provider`, `builder.Tools`, `builder.Middleware`) are **optional discoverability sugar**, not structural.

   ```csharp
   await DmonHost.CreateBuilder()                       // flat marquee chain
       .UseOpenAI("gpt5.5")
       .AddAgentWebSearch("gemini-3.5-flash-lite")
       .Build().RunAsync();
   ```

   A **canonical verb grammar** is binding for blessed and third-party verbs alike: `Use<X>` replaces a single strategy (provider, model, system prompt, memory); `Add<X>` appends to a collection (tool extension, middleware); `With<X>`/`Append<X>` set a scalar / mutate a composed value. `WithModel` is renamed `UseModel`; `AddExtension` becomes `AddToolExtension`.

5. **Registration moves onto DI-discovery; the post-build manual loops are deleted.** `AddDmonCore` (and `AddDmonProviders`/`AddDmonExtensions`) enumerate `IEnumerable<IToolExtension>`, `IEnumerable<IDmonMiddleware>`, and `IEnumerable<IProviderExtension>` from the container at build time and route each into `IToolRegistry`, `IMiddlewareRegistry`, and `IProviderRegistry` (via the already-existing `IProviderRegistry.RegisterExtensionAsync`, gated by `IsApplicable`). `AddToolExtension<T>` is `Services.AddSingleton<IToolExtension,T>()`; `AddMiddleware<T>` and `AddProvider<T>` likewise. This is the substrate that makes Decision 3 possible and **closes ADR-019's `IProviderExtension` orphan** — provider extensions finally have a composition entry point.

6. **Sub-agent providers are configured through the *same* `IProviderRegistration`, in isolation.** A tool that runs its own model accepts `Action<IProviderRegistration>` and reuses the provider verbs:

   ```csharp
   public static T AddAgentWebSearch<T>(this T t, string model) where T : IToolRegistration
       => t.AddToolExtension(new WebSearchExtension(p => p.UseGemini(model)));
   ```

   The action runs against a **fresh, isolated** `IProviderRegistration` that materializes to an `IChatClientFactory` (a minimal `ValueTask<IChatClient> CreateAsync(CancellationToken)`, declared in `Dmon.Abstractions`) which the tool captures and invokes per call. This factory **never touches `IProviderRegistry`** — making ADR-010 Decision 3's independence rule *structurally enforced* rather than merely documented. ADR-010's scope boundary is retained in full (single-turn, scoped, not orchestration); only its "no new contract type" consequence is reversed, because the facet model makes the one type (`IChatClientFactory`) worth its weight by both removing the boilerplate and guaranteeing independence.

7. **Provider composition is symmetric (Option B): nothing is baked.** Cloud (`IProviderFactory`) and local (`IProviderExtension`) providers are *both* composed via `Use<Provider>` verbs; the verb grammar hides the shape difference (a `UseOpenAI` registers a factory; a `UseLlamaCpp` registers a lifecycle extension). Each provider ships as its **own granular implementation package** (`Dmon.Providers.<Name>`) carrying its vendor SDK, leaving `dmoncore` vendor-SDK-free — the package topology, naming, versioning, and sub-agent-coupling norms are ADR-023. The stock default `Dmon.cs` lists its providers explicitly (consistent with ADR-019 Decision 9; see Decision 13). The **provider-name prefix is a contract**: `UseLlamaCpp("gemma")` selects `"llamacpp/gemma"`, so the `Use…` verb's prefix must equal the provider's registered `ProviderName`/`AdapterName`.

8. **Tool extensions are DI-constructed.** `AddToolExtension<T>` drops the `new()` constraint and instantiates via `ActivatorUtilities.CreateInstance` (as middleware already does), so a tool may inject host services. `CreateBuilder()` gains a parameterless overload (args default to `[]`).

9. **The contract is renamed `IDmonExtension` → `IToolExtension`.** The shape (`Name`, `Description`, `IEnumerable<AIFunction> Tools`, `Evaluate`, `CreateConfirmRequest`) is unchanged; only the name changes, to disambiguate "the tools bundle" from "an extension package in general." This is a clean break (no production deployments; back-compat not required).

10. **The sub-agent `IChatClientFactory` validates eagerly but resolves lazily.** At `Build()`, each sub-agent `IProviderRegistration` is checked for *structural* validity — exactly one provider verb invoked and a model selected — and a malformed one fails the build (an author error, e.g. an empty `Action<IProviderRegistration>`). Credential resolution (ADR-005 `DefaultEnvVar`) and `IChatClient` construction are deferred to the first `CreateAsync`, which throws `InvalidOperationException` naming the missing env var if the key is absent. Rationale: a sub-agent tool that is never invoked must not block core startup, so credentials cannot be a boot dependency; but a structurally broken composition root should not survive to runtime. The factory **may memoize** the constructed `IChatClient` — "single-turn" (ADR-010 D4) is a property of how the tool *uses* the client (a fresh message list per call), not of client lifetime; the runtime keeps nothing between calls regardless.

11. **There is no "persona" concept; the system prompt is a plain string.** It resolves from a single `systemPrompt` value on `IConfiguration` — supplied via `config.yaml`, an environment variable, or `args` at startup — as the declarative default, overridden in code by `UseSystemPrompt(string)` (replace the base) or extended by `AppendToSystemPrompt(string)` (ordered append), per ADR-019 Decision 6 ("config declares, C# overrides"). The resolved string *is* the system prompt: `final = (UseSystemPrompt value, else config systemPrompt, else the core's built-in default) + ordered appends`. The core injects **no hidden scaffolding** into it — tools reach the model through M.E.AI `ChatOptions`, not the prompt — so what you set is what the model sees. Example: `…CreateBuilder().UseSystemPrompt("You are a tiny beetle…")`. The system prompt thus stops being a "persona" and becomes one more composed value; the broader removal of the profile/definition bundle is Decision 14. Total control beyond replace/append remains via the `Services.AddSingleton<ISystemPromptBuilder>(…)` escape hatch.

12. **`Dmon.Extensions` is deleted; all author-facing contracts collapse into `Dmon.Abstractions`.** `IToolExtension`, `IDmonMiddleware`, the three facet interfaces, `IDmonHostBuilder`, `DmonMiddlewareAttribute`, and `DmonAIFunctionFactory` move into `Dmon.Abstractions`, joining the provider/memory/system-prompt contracts already there. The published SDK base becomes **`Dmon.Protocol` + `Dmon.Abstractions` + `dmoncore`** — one contract package every extension author references, one protocol-keyed version line to pin (Decision 7's versioning). This is the right trade *because of* the granular implementation-package split (Decision 7 / ADR-023): with vendor SDKs and concrete factories in their own packages, the contract package carries no weight, so per-facet contract packages would buy only weightless interface separation while costing real package/versioning ceremony — and the tool→provider boundary they'd "protect" is already crossed by sub-agent tools (Decision 6). *(This reverses an earlier draft of this decision that kept `Dmon.Extensions` distinct; the granular-implementation-package commitment is the new information that changes the calculus.)*

13. **The stock default `Dmon.cs` lists its providers explicitly.** The canonical, scaffolded composition root reads `…CreateBuilder().UseAnthropic().UseOpenAI().UseGemini().UseOllama()…` rather than a single opaque aggregate — legibility of "what is this agent made of" is the feature's central value, and an explicit list is self-documenting and trivially editable. A blessed `.AddDefaultProviders()` *may* ship as convenience sugar equivalent to that list, but it is not what the scaffold emits. Cloud providers register unconditionally (no `IsApplicable` lifecycle); model selection among the registered providers stays config/wizard-driven (ADR-005/007), unchanged.

14. **An agent *is* its `.cs` composition root — there are no profiles, personas, or `.md` definitions.** The whole "agent profile" (ADR-013) / "agent definition" (ADR-020) bundle collapses into the composition root, which is the endpoint of ADR-019's "composition is code":
    - **Default agent** = the root `Dmon.cs`. **Named agents** = `.dmon/agents/<name>.cs` composition roots (the `.md` pairing of ADR-020 is gone).
    - **Per-session selection** is preserved: a session selects an agent *name*, and the launcher (`ICoreLauncher`, ADR-019 Decision 4) builds and runs that `.cs`; a gateway session resolves the name under its configured workspace root (ADR-019 Open Question E), never a client-supplied path. Selecting a different agent runs a different composition root — consistent with single-core (ADR-010 holds; these are not RPC-peer processes).
    - Every capability the bundle used to carry becomes a **builder verb in the `.cs`**: system prompt → `UseSystemPrompt`/config (Decision 11); permission mode → `WithPermissionMode` (already present); session assets → a builder verb (e.g. `UseAssets(path)`). The `WithProfile` verb and the `IAgentProfileResolver`/profile-config machinery are removed.
    - The gateway `createSession` contract's `profile` parameter becomes an `agent` (name) selector; its resolution is otherwise unchanged. The former built-in `coding` profile is simply the default `Dmon.cs`.

## Consequences

- **The composition root becomes open and grows without core edits.** Any package can ship verbs; the builder is the union of facets it implements. This is the feature the ADR exists to enable.
- **One abstraction, two contexts.** `IProviderRegistration` is the root's provider surface *and* a sub-agent's provider surface; the sub-agent's isolation is the same isolation a fresh registration always has. Gaps 2 and 3 (no arbitrary-model factory; sub-agent provider availability) dissolve into one mechanism.
- **A net deletion at the seam.** The builder's post-build `IToolRegistry`/`IMiddlewareRegistry` replay loops are removed in favour of DI enumeration; provider registration stops being a special baked path.
- **Wide but mechanical rename churn.** `IDmonExtension` → `IToolExtension` and `AddExtension`/`WithModel` → `AddToolExtension`/`UseModel` touch the sample extension, the two `Dmon.cs` roots, docs, and tests. The published contract-package surface changes — acceptable per the no-back-compat stance, but it is a breaking SDK change for any out-of-tree extension.
- **`Dmon.Extensions` is deleted (Decision 12).** Its types fold into `Dmon.Abstractions`; the SDK base is `Dmon.Protocol` + `Dmon.Abstractions` + `dmoncore`. Provider/memory/tool/middleware contracts now share one package — interface visibility, not deployment weight, since the vendor SDKs live in the granular implementation packages (ADR-023).
- **`IDmonHostBuilder` lives in `Dmon.Abstractions`** alongside the facets it aggregates — which vindicates this session's original "put it in `Dmon.Abstractions`" instinct, now that everything author-facing is there.
- **The profile/persona subsystem is deleted (Decision 14).** Out go `IAgentProfileResolver`, `AgentProfile`, the persona-`.md` loading, `ProfilesConfigReader`/`EffectiveProfileSetResolver`/`AgentProfileContext`, the `WithProfile` builder verb, and `DmonHostBuilder`'s `ProfileOverrideResolver` decorator. The `PermissionMode` enum survives (it backs `WithPermissionMode`); `ISessionAssetProvisioner` survives behind a new assets verb. This is a large, decisive subtraction — the single biggest churn in the change — but it removes an entire declarative layer that ADR-019 had already made redundant.
- **A wire-contract touch.** Gateway `createSession`'s `profile` field becomes `agent`; this is a breaking protocol change (acceptable per the no-back-compat stance) and must land in `openspec/specs/` alongside the code.

## Alternatives

- **One fat `IDmonHostBuilder` with all typed members (no facets).** Rejected: it cannot live anywhere both provider and tool packages reach without a union package, and it cannot be reused as the sub-agent provider surface — the facet split is what makes `Action<IProviderRegistration>` possible.
- **Keep manual post-build registration, just extract an interface.** Rejected: extension methods can't hook a private loop, so the surface would stay effectively closed — the headline feature would not materialise.
- **Keep cloud providers baked (Option A asymmetry).** Rejected per this session's decision to make composition a product feature; symmetry (Decision 7) is the point, and the two provider *shapes* are still honoured behind the verb grammar.
- **Resolve sub-agent clients via `IEnumerable<IProviderFactory>` as ADR-010 prescribed, with no new type.** Rejected: it leaves D3's independence unenforced and the boilerplate in every tool; the `IChatClientFactory` both removes the boilerplate and makes corruption of the root registry impossible.

## Open Questions

- **A. ~~`IChatClientFactory` materialization timing.~~** *Resolved by Decision 10* — eager structural validation at `Build()`, lazy credential resolution and client construction at first `CreateAsync`; client may be memoized.
- **B. ~~`UseSystemPrompt` vs. profile/persona precedence (ADR-013/020).~~** *Resolved by Decision 11* — persona is dropped entirely; the system prompt is a plain string from `IConfiguration` (`config` < `UseSystemPrompt`, with `AppendToSystemPrompt` composing), with a raw-DI escape hatch.
- **C. ~~Does `Dmon.Extensions` survive as a distinct package?~~** *Resolved by Decision 12* — no; it is deleted and all author-facing contracts collapse into a single `Dmon.Abstractions`, the vendor weight having moved to the granular implementation packages (ADR-023).
- **D. ~~`.AddDefaultProviders()` vs. explicit lists in the stock `Dmon.cs`.~~** *Resolved by Decision 13* — the scaffold lists providers explicitly for legibility; the aggregate is optional sugar only.

*No open questions remain.*

## Relationship to other ADRs

- **ADR-002** — the tools contract is retained in shape and renamed `IToolExtension`; the `AIFunction` surface is unchanged.
- **ADR-007** — `IProviderExtension` and `IProviderFactory` are unchanged; `IProviderExtension` finally gets a composition entry point (Decision 5) and `IProviderRegistry.RegisterExtensionAsync` becomes the build-time routing call.
- **ADR-010** — scope boundary (single-turn, independent, not orchestration) retained and now *enforced* by `IChatClientFactory`'s isolation; the "no new contract type" consequence is reversed with rationale.
- **ADR-019** — the hosting surface of Decision 1 is restructured (facets, DI-discovery, verb grammar); the file-based-program composition model, `--no-build` wire discipline, launcher precedence, and stock-default packaging are unchanged.
- **ADR-013** — **superseded in full** (Decision 14): the agent-profile-as-named-bundle concept is dissolved into builder verbs (`UseSystemPrompt`, `WithPermissionMode`, an assets verb) and `.cs` selection. The former built-in `coding` profile is the default `Dmon.cs`.
- **ADR-020** — its `.md`-persona + bundle framing is **superseded** (an agent is its `.cs`); its per-session selection model, `.dmon/agents/` location, and gateway-workspace-root resolution (ADR-019 OQ E) are **retained**, now keyed to `.cs` composition roots.
- **ADR-021** — agent-authored changes to provider/tool/middleware composition remain gated by the apex `compose` tier; this ADR only restructures the *author-time* surface, not the runtime gate. Note the gate now also covers an agent editing its own `UseSystemPrompt`/persona-equivalent, since that lives in the `.cs`.
- **ADR-012** — `createSession`'s `profile` selector becomes an `agent` selector (Decision 14); transport, resume, and the `ICoreLauncher` seam are otherwise unchanged.
