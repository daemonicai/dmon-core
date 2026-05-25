## Context

dmon already supports two implicit extension tiers: **Tools** (`IDmonExtension` exposing `AIFunction` instances) and **Providers** (bare `IChatClient` implementations). `Microsoft.Extensions.AI` makes a third tier natural: the `IChatClient` pipeline model lets any client wrap another, intercepting every request and response unconditionally.

The extension loader currently discovers `IDmonExtension` implementations in `.csx` scripts and NuGet packages. The pipeline is currently a single provider client with no wrapping. Configuration is via env vars and a typed config file.

## Goals / Non-Goals

**Goals:**
- Add `IDmonMiddleware` and `[DmonMiddleware]` to the `Dmon.Contracts` assembly so authors can write middleware extensions.
- Discover middleware in the same extension packages/scripts as tools.
- Build the `IChatClient` pipeline from discovered middlewares at agent startup.
- Allow per-middleware configuration via named YAML sections; allow priority to be overridden in config.
- Inject `IServiceProvider` (with `IConfigurationRoot`) into middleware so each can read its own config.

**Non-Goals:**
- Hot-reload of middleware (restart required; tool hot-reload is unaffected).
- Middleware-specific permission prompts (middleware is trusted code, same as tools).
- Any changes to the RPC protocol, session storage, or permission model.

## Decisions

### D1 — Interface shape: single `Wrap` method

```csharp
public interface IDmonMiddleware
{
    IChatClient Wrap(IChatClient inner);
}
```

**Rationale:** The M.E.AI `IChatClient` interface already defines the full request/response contract. `Wrap` is the minimal surface that gives middleware authors complete control while keeping the composition model simple: `middlewares.Aggregate(baseClient, (inner, m) => m.Wrap(inner))`. A richer interface (e.g., `ConfigureAsync`, `OnTurn`) would duplicate what the `IChatClient` wrapper can already express.

**Alternative considered:** A base class `DmonMiddlewareBase : IChatClient` providing default pass-through implementations. Rejected — forces inheritance where composition is sufficient, and M.E.AI's `DelegatingChatClient` already serves this role for authors who want a head start.

### D2 — Discovery: attribute on the class

```csharp
[DmonMiddleware(Priority = 100)]
public class MyCachingMiddleware : IDmonMiddleware { ... }
```

The extension loader reflects over loaded types, finds those implementing `IDmonMiddleware` and annotated with `[DmonMiddleware]`, and instantiates them. This is symmetric with how `IDmonExtension` is discovered.

**Rationale:** Attribute-based discovery is already the pattern in the codebase. Middleware that forgets the attribute is simply not loaded — a clear contract.

### D3 — Priority and pipeline order

Lower `Priority` value = closer to the base provider (innermost); higher value = closer to the caller (outermost). Default attribute value is `0`. Config can override per-middleware. Fold order: sort ascending by priority, then fold: `middlewares.OrderBy(m => m.Priority).Aggregate(baseClient, (inner, m) => m.Wrap(inner))`.

**Rationale:** "Priority 0 is innermost" mirrors the intuition that lower-level concerns (retry, caching) run close to the wire, while higher-level concerns (observability, guardrails) run close to the caller. Config override lets operators tune ordering without recompiling.

**Alternative considered:** Declared ordering via `[DmonMiddleware(RunsBefore = typeof(X))]`. Rejected as over-engineered for V1 — a simple integer covers all practical cases.

### D4 — Service injection: constructor `IServiceProvider`

The host instantiates middleware by calling `Activator.CreateInstance(type, serviceProvider)` (or a no-arg constructor if the type doesn't accept `IServiceProvider`). Middleware that needs config calls `serviceProvider.GetRequiredService<IConfigurationRoot>().GetSection("middleware:<name>")`.

**Rationale:** Constructor injection keeps middleware testable (pass a test `IServiceProvider`). Avoiding a dedicated `Initialize` method simplifies the loader — no two-phase construction. Supporting both overloads (with and without `IServiceProvider`) means simple middleware stays simple.

### D5 — Same contract assembly as `IDmonExtension`

`IDmonMiddleware` and `DmonMiddlewareAttribute` live in `Dmon.Contracts` alongside `IDmonExtension`.

**Rationale:** A single contract NuGet package is simpler for extension authors. The two interfaces are complementary (an extension package could expose both tools and middleware), and splitting them would create versioning friction with no benefit in V1.

### D6 — No hot-reload for middleware

Middleware changes require a process restart. The agent startup sequence constructs the pipeline once and holds a stable reference. File-watcher events for middleware assemblies are ignored.

**Rationale:** The pipeline is structural — swapping middleware mid-session risks state loss (caches, counters) and mid-stream inconsistency. The added complexity of double-buffering or drain-and-swap is not justified for V1. This is documented clearly so authors know what to expect.

## Risks / Trade-offs

- **[State loss on reload]** Because there is no hot-reload, middleware with in-memory state (e.g., a semantic cache) loses that state on restart. → Accepted for V1. Middleware authors who need persistence should write to disk or an external store.
- **[Instantiation failures]** A middleware that throws in its constructor will prevent agent startup. → The loader SHALL catch construction exceptions, log them, and skip the failing middleware (not abort startup). This matches the existing tool-loader behaviour.
- **[Priority collisions]** Two middlewares with the same priority have undefined relative order. → Document that priority values should be spaced (e.g., 100, 200) to allow insertion. Stable sort preserves registration order as a tiebreaker.
- **[`DelegatingChatClient` dependency]** If middleware authors use M.E.AI's `DelegatingChatClient` base class, they take a dependency on a specific M.E.AI version. → This is expected and acceptable; `Dmon.Contracts` already pins M.E.AI.

## Open Questions

- Should `Dmon.Contracts` re-export `DelegatingChatClient` or reference M.E.AI directly? (Current assumption: direct M.E.AI reference, no re-export.)
- Is there a need for middleware to signal "I am not applicable for this turn" (e.g., skip caching for streaming)? If so, a thin `ShouldWrap(ChatOptions)` hook could be added in a later change.
