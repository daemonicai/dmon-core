## Context

The current composition root (ADR-019) exposes a sealed `DmonHostBuilder` in `Dmon.Core` with a fixed, ad-hoc method set; registration of extensions and middleware happens in manual post-`Build()` loops over private lists; providers are baked (`AddDmonProviders` registers four `IProviderFactory` singletons + vendor SDKs referenced directly by `Dmon.Core`), and `IProviderExtension` has no entry point at all. Profiles/personas (ADR-013/020) are a separate declarative subsystem (`IAgentProfileResolver`, `AgentProfile`, persona `.md`, profile-config readers). ADR-022 and ADR-023 (Accepted) replace this with an open, DI-backed, faceted builder and a granular package topology; an agent becomes its `.cs` composition root with no persona concept. This change implements both ADRs. There are no production deployments (clean breaks allowed; no migration/back-compat).

## Goals / Non-Goals

**Goals:**
- An open builder: any package contributes `UseX`/`AddX` verbs as extension methods; no core edit per new provider/tool/middleware.
- Make `IDmonHostBuilder` a thin `{ Services, Configuration }` wrapper; move all registration onto DI-discovery; delete the post-build manual loops.
- Compose all providers (Option B); make `dmoncore` vendor-SDK-free; ship each provider/tool as its own package.
- Supply ADR-010's sub-agent mechanism via `Action<IProviderRegistration>` → isolated `IChatClientFactory`.
- Dissolve profiles/personas: system prompt is a plain string; an agent is its `.cs`.
- Rename `IDmonExtension`→`IToolExtension`; delete `Dmon.Extensions`, collapsing contracts into `Dmon.Abstractions`.

**Non-Goals:**
- Multi-agent orchestration (ADR-010 deferral holds; sub-agents stay single-turn, single-core).
- Changing the wire protocol beyond `createSession` `profile`→`agent`.
- Runtime extension loading (ADR-019 already removed it; composition stays compile-time `#:package`).
- A skill marketplace / discovery service (out of scope per the brief).
- Splitting `Dmon.Tools.Builtin` into sub-packages (ADR-023 Open Question A; deferred).

## Decisions

All architectural decisions are fixed by **ADR-022** and **ADR-023** — this section records the implementation-shaping choices and their ADR provenance; it does not re-open them.

- **Facet interfaces + self-type generic verbs (ADR-022 D1–D4).** `IProviderRegistration` / `IToolRegistration` / `IMiddlewareRegistration` are declared in `Dmon.Abstractions`; `IDmonHostBuilder` aggregates all three plus `Services`/`Configuration`. Verbs are extension methods `static T Verb<T>(this T r, …) where T : IFacet`, so calling on the concrete builder (which implements all facets) returns the builder type (flat chaining) and calling on a bare facet returns the facet (sub-agent reuse). Blessed verbs ship in the `Dmon.Hosting` namespace (ADR-023 D4) so they appear after `#:package` + the existing `using`.
- **DI-discovery substrate (ADR-022 D5).** `AddDmonCore` enumerates `IEnumerable<IToolExtension>` / `IDmonMiddleware` / `IProviderExtension` and routes them (tools→`IToolRegistry`, middleware→`IMiddlewareRegistry`, providers→`IProviderRegistry.RegisterExtensionAsync` gated by `IsApplicable`). `AddToolExtension<T>`/`AddMiddleware<T>`/`AddProvider<T>` are thin `Services.AddSingleton<IFacetContract,T>()` calls; the post-build loops in `DmonHostBuilder` are deleted.
- **Sub-agent isolation (ADR-022 D6, D10).** `Action<IProviderRegistration>` runs against a *fresh isolated* registration that materializes to a captured `IChatClientFactory` (`ValueTask<IChatClient> CreateAsync(CancellationToken)`, in `Dmon.Abstractions`), never touching `IProviderRegistry`. Structural validity (one provider verb, a model) is checked at `Build()`; credential resolution + client construction are lazy at first `CreateAsync` (`InvalidOperationException` names the missing env var); the client may be memoized.
- **Option-B provider symmetry + granular packages (ADR-022 D7, ADR-023 D1–D2).** Cloud (`IProviderFactory`) and local (`IProviderExtension`) are both composed via `Use<Provider>`; the verb prefix must equal the provider's registered name. Each provider moves to `Dmon.Providers.<Name>` carrying its vendor SDK; `Dmon.Core` sheds the four vendor SDK refs and `AddDmonProviders`.
- **DI-constructed tools (ADR-022 D8).** `AddToolExtension<T>` drops `new()`, instantiating via `ActivatorUtilities` (as middleware already does); `CreateBuilder()` gains a parameterless overload.
- **Contracts collapse (ADR-022 D12).** Delete `Dmon.Extensions`; move `IToolExtension`, `IDmonMiddleware`, the facets, `IDmonHostBuilder`, `DmonMiddlewareAttribute`, `DmonAIFunctionFactory` into `Dmon.Abstractions`. SDK base = `Dmon.Protocol` + `Dmon.Abstractions` + `dmoncore`.
- **No persona; agent = `.cs` (ADR-022 D11, D14).** System prompt resolves from `IConfiguration["systemPrompt"]`, overridden by `UseSystemPrompt`/`AppendToSystemPrompt`; no hidden scaffolding (tools ride `ChatOptions`). The profile subsystem is deleted (`IAgentProfileResolver`, `AgentProfile`, `ProfilesConfigReader`, `EffectiveProfileSetResolver`, `AgentProfileContext`, `WithProfile`, `ProfileOverrideResolver`); `PermissionMode` survives behind `WithPermissionMode`; `ISessionAssetProvisioner` survives behind a new `UseAssets` verb. `createSession`'s `profile`→`agent`.
- **Versioning (ADR-023 D5).** Contracts + first-party packages move lockstep on `dmoncore`'s protocol-keyed `Major.Minor` line; incompatible `#:package` sets fail at `dotnet restore`; the `agentReady` gate is the backstop for the prebuilt stock path.

## Risks / Trade-offs

- **Very large, cross-cutting, breaking change.** → Group tasks so each group builds + tests green independently; sequence so contracts/rename land before consumers. No back-compat needed (no deployments).
- **Profile-subsystem deletion may touch session/gateway code paths beyond the obvious.** → Audit all `IAgentProfileResolver`/`AgentProfile`/`profile` references first (a discovery task) before deleting; convert each to a builder verb or `agent` selector.
- **DI-discovery ordering vs. existing `TryAdd`/decorator logic** (e.g. stdio `TextWriter`, permission-mode resolver). → Preserve the `TryAdd`-yields-to-builder ordering; the permission-mode override decorator stays (only the profile override is removed).
- **Provider package split could regress the setup wizard / model listing** which currently resolve factories from DI. → They already operate on abstractions; verify enumeration still finds the now-package-supplied factories.
- **Sub-agent `IChatClientFactory` lazy errors surface at tool-call time.** → Documented per ADR-022 D10; structural validation at `Build()` catches author mistakes early.
- **`Dmon.Hosting` verb-namespace collisions** between packages. → Convention only; verbs are named per provider/tool, collisions are an author error surfaced at compile time.

## Migration Plan

No runtime data migration (no persisted profile state of consequence; clean break). Implementation/rollout sequence:
1. **Contracts first** — collapse into `Dmon.Abstractions` (rename `IToolExtension`, move types, declare facets + `IDmonHostBuilder` + `IChatClientFactory`), delete `Dmon.Extensions`. Everything downstream compiles against the new surface.
2. **Builder + DI-discovery** — thin `IDmonHostBuilder`, verb extension methods, registry enumeration; delete post-build loops.
3. **Provider split + Option B** — extract `Dmon.Providers.<Name>` packages, vendor-free `Dmon.Core`, `Use<Provider>` verbs, delete `AddDmonProviders`.
4. **Sub-agent factory** — isolated `IProviderRegistration` → `IChatClientFactory`; DI-constructed tools.
5. **Profile demolition** — delete the profile subsystem; system prompt as config/verb; `UseAssets`; `createSession` `profile`→`agent`.
6. **Builtin-tools + scaffold** — `Dmon.Tools.Builtin` + `AddBuiltinTools`; update `default-core/Dmon.cs`, sample, scaffold output; sync standing specs.

Rollback = revert the change branch; no external state touched.

## Open Questions

None blocking. ADR-023's open questions (builtin-tools sub-splitting; a shared `Dmon.Providers.Core`; memory always-on vs opt-in) are deferred and out of scope for this change.
