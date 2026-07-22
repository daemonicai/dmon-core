## Context

`Dmon.Providers.Mlx` exposes two ways to obtain an MLX-backed `IChatClient`:

1. **Active-provider path** — `UseMlx(modelId, port)` calls `AddProvider(new MlxProviderExtension(options)).UseModel("mlx", modelId)`. Per turn, `core`'s `TurnHandler` resolves the client via `ProviderRegistry.GetCurrentAsync()` → `MlxProviderExtension.CreateFactory()` → `MlxProviderFactory.CreateAsync()`.
2. **Keyed router-backend path** — the daemon registers runtimes via `AddMlxFirstline`/`AddMlxEscalation` and builds clients through `MlxClient(key)` (`MlxClientExtensions.cs`).

The self-heal wrapper `EnsureRunningChatClient` (which calls `MlxProviderExtension.EnsureRunningAsync()` before each request) is wired **only** in path 2. `MlxProviderFactory.CreateAsync` builds `ChatClient → MlxMaxTokensDefaulter → CapabilitiesDecorator` and stops. Nothing in `core/` ever calls `IProviderExtension.EnsureRunningAsync` on the active provider (verified: the method appears only in the interface declaration across `core/`).

Consequences on path 1:
- The runtime is never started → the first request hits a dead port (`Connection refused`).
- `EnsureRunningAsync` runs the tool-calling probe that sets `MlxRuntimeState.ToolCallingVerified`. Never called ⇒ `MlxProviderFactory.GetCapabilities()` reads `ToolCallingVerified == null` ⇒ `SupportsToolCalling == false`, snapshotted once into `CapabilitiesDecorator`. So even a hand-started server would leave tools disabled.

`EnsureRunningChatClient`'s own doc comment already declares it "sits outermost over the … decorator stack built by `MlxProviderFactory`" — the design intent was for the factory to own the wrapper; it was only ever wired into the keyed helper.

## Goals / Non-Goals

**Goals:**
- The active-provider terminal client starts and self-heals the MLX runtime (attach-first), matching the keyed path.
- The tool-calling probe runs before capabilities are snapshotted, so `SupportsToolCalling` is correct on path 1.
- Self-heal is single-sourced in the factory; the keyed helper stops duplicating it.
- Keyed daemon behaviour (`AddMlxFirstline`/`AddMlxEscalation`, `UseTriage`/`AddEscalation`, `EscalationWarmingService`) is unchanged.

**Non-Goals:**
- No changes to `core/` (`TurnHandler`, `ProviderRegistry`, `IProviderExtension`).
- No changes to routing, warming, or the keyed-runtime registration seams.
- No ADR changes (ADR-027 terminal-client selection, ADR-034 MLX lifecycle both honoured).
- Not generalising self-heal to other providers — scope is `Dmon.Providers.Mlx`.

## Decisions

### D1 — Self-heal in the factory, not core

Put `EnsureRunningAsync` + `EnsureRunningChatClient` in `MlxProviderFactory.CreateAsync`, keyed off an `MlxProviderExtension` reference the factory now holds. `MlxProviderExtension.CreateFactory()` passes `this`.

*Why not core?* Adding an `EnsureRunningAsync` call to `ProviderRegistry`/`TurnHandler` activation would touch the core contract for all providers and duplicate a provider concern the MLX types already own. The factory is where MLX already assembles its client stack, and `EnsureRunningChatClient` was designed to live there. Rejected: a core-level "activate provider" hook (larger blast radius, ADR-scope).

### D2 — Ordering: `EnsureRunningAsync` before the capabilities snapshot

`CreateAsync` must `await extension.EnsureRunningAsync(ct)` **first**, then read `GetCapabilities()` (which now sees `ToolCallingVerified` set by the probe), then build the decorator stack, then wrap outermost in `new EnsureRunningChatClient(inner, extension)`. This makes the capability snapshot correct on the first construction and keeps per-request self-heal thereafter.

*Why not lazy (self-heal only per request, no up-front call)?* `CapabilitiesDecorator` captures a capability snapshot at construction; a lazy client built before the probe would advertise `SupportsToolCalling == false` and the agent would never offer tools. So the up-front `EnsureRunningAsync` is required for correctness, not just latency. Attach-first makes it cheap when a server is already up.

### D3 — `CreateAsync` becomes genuinely async

`CreateAsync` currently returns `ValueTask.FromResult(...)`. It now awaits `EnsureRunningAsync`, so it does real async work. Signature is unchanged (already returns `ValueTask<IChatClient>`); callers already await it.

### D4 — Simplify `MlxClient(key)` to single-source the self-heal

`MlxClient(key)` currently calls `EnsureRunningAsync` up front and wraps the factory output in its own `EnsureRunningChatClient`. With D1/D2, the factory already does both. Reduce `MlxClient` to: resolve the keyed `MlxProviderExtension`, build the `ProviderConfig`, and return `factory.CreateAsync(...)` — no duplicate call, no duplicate wrap. Net daemon behaviour is identical (still attach-first self-healing), now single-sourced.

*Why not leave `MlxClient` double-wrapping?* Double `EnsureRunningAsync` is idempotent/cheap and a double wrapper is harmless, but two self-heal sources invite drift and confuse the "single source of truth" invariant. Simplifying is low-risk and clearer.

### D5 — Clean break on internal signatures (no shim)

`MlxProviderFactory`'s public/internal constructors gain an `MlxProviderExtension` parameter. Both types are first-party, unpublished-consumer internals; per "no production deployments" there is no back-compat obligation. Update all construction sites (production `CreateFactory` + the internal test ctors/seams) directly.

## Risks / Trade-offs

- **Up-front model load blocks the first `CreateAsync`** → Mitigation: attach-first means it is a fast liveness check when a server is already running; on cold start the first turn legitimately waits for the model to load (the alternative — a dead port — is worse). Bounded by the existing `ReadyTimeout`.
- **Factory now depends on the extension (tighter coupling)** → Mitigation: this mirrors the already-shipped `MlxClient` relationship and the documented design intent; the extension is the natural owner of the runtime lifecycle.
- **Test seams that construct `MlxProviderFactory` directly must all be updated** → Mitigation: a mechanical signature change; add focused coverage that `CreateAsync` invokes the start path and returns a self-healing client, and that `MlxClient` no longer double-wraps.
- **Double self-heal removed from `MlxClient`** → Mitigation: behaviour is preserved because the factory now provides it; regression covered by a keyed-path test asserting the returned client still self-heals.

## Migration Plan

Single-commit block (bug fix, one component). No runtime data/config migration. Rollout: rebuild `Dmon.Providers.Mlx`; the `sandbox-code/Agent.cs` harness then works via the unchanged `.UseMlx(...)`. Rollback: revert the commit (no persisted state, no protocol/wire change).

## Open Questions

None.
