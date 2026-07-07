## Why

The MLX-backed triage/escalation path has an interlocking cluster of concurrency and resilience defects (repo audit 2026-07-06, finding #2; all three re-verified present on `main@b1a29f6`). A single transient first-turn failure permanently poisons a routing backend, the documented "self-heal after idle teardown" does not actually exist on the request path, and unsynchronized server spawning can orphan an mlx_lm process that no code can ever reap. Each defect degrades the local-model daemon from a recoverable transient error into a dead-until-process-restart state.

## What Changes

- **Fault-recovering backend resolution.** Replace `TriageRouter`'s poisoning `Lazy<Task<IChatClient>>` per-backend cache with a resolver that re-resolves a backend whose previous resolution attempt **faulted**, while still caching a successful resolution once. Construction stays I/O-free (ADR-027 D1 / ADR-032 D3); only the first await resolves.
- **Real request-path respawn.** Make the first-line and escalation **request paths** ensure-running per turn so a backend torn down by the idle timer (or otherwise dead) is respawned on demand; attach-first keeps the healthy path cheap. Correct the now-inaccurate `EscalationWarmingService` "self-heal path" comment/contract so it describes real behavior.
- **Serialized, idempotent spawn.** Add a `SemaphoreSlim(1,1)` gate around the check-then-spawn critical section of `MlxProviderExtension.EnsureRunningAsync` (double-checked not-running inside the gate) so concurrent callers spawn **exactly once**, eliminating orphaned mlx_lm processes and concurrent `uv pip install` on one venv. Keep `StopAsync`/`Dispose` interplay correct under the gate.
- **Tests.** Add coverage for the three gaps: transient-then-succeed backend recovery, post-idle-teardown respawn on the next request, and concurrent `EnsureRunningAsync` spawning once with no orphan.

No wire-protocol, RPC, or persistence changes. No public author-facing verb (`UseTriage`/`AddEscalation`/`AddEgress`/`AddMlx*`) signatures change.

## Capabilities

### New Capabilities

_None — this is a correctness/resilience change to existing behavior._

- `triage-routing`: **ADDED** — backend resolution must **recover from a transient resolution fault** (a faulted attempt is not cached forever, while a success is cached once); **MODIFIED** — the escalation **request path must ensure the runtime is running before dispatching**, so a runtime torn down for idle is respawned on demand (making the previously-broken "self-heal" real and testable).
- `mlx-provider`: **ADDED** — `EnsureRunningAsync` must be **safe under concurrent invocation**: concurrent callers on the same fixed port spawn exactly one server process, provision the venv once, and leave no orphaned process.

### Conformance-only (no spec delta)

- `escalation-warming`: its existing requirement "Warming is an optimization, not a correctness dependency" **already** mandates that the escalation request path ensures the runtime is running (line 34). Finding #2 is the *code* not honoring that spec; the fix is conformance, captured by the `triage-routing` MODIFIED delta above. No change to the warming service's own contract.
- `provider-extension`: the concurrency-safety guarantee is kept **MLX-local** (`mlx-provider`) rather than generalized onto the `IProviderExtension` (ADR-007) lifecycle contract, keeping this change tightly scoped.

## Impact

- **Code:** `daemon/Daemon.Routing/TriageRouter.cs`, `daemon/Daemon.Routing/EscalationWarmingService.cs`, `providers/Dmon.Providers.Mlx/MlxProviderExtension.cs`, `providers/Dmon.Providers.Mlx/MlxClientExtensions.cs`.
- **Tests:** `test/Daemon.Routing.Tests/TriageRouterTests.cs`, `test/Daemon.Routing.Tests/EscalationWarmingServiceTests.cs`, `test/Dmon.Providers.Mlx.Tests/MlxProviderExtensionTests.cs`.
- **ADRs:** touches ADR-007 (provider `EnsureRunningAsync` lifecycle), ADR-032 (triage routing / lazy factory shape), ADR-034 (MLX fixed-port runtime + activity-warming + idle-teardown). Intent is to **amend** the affected standing specs (and, if needed, add an amendment note to an ADR) — not to supersede an ADR. The per-turn request-path respawn must be confirmed consistent with ADR-034 D2 ("warming never blocks a session command or turn"): the escalation **turn** respawning its own backend is distinct from background warming. If the design surfaces a genuine ADR contradiction, that is a stop-and-ask (a superseding ADR must be accepted first).
- **No impact:** wire protocol, session storage, gateway/network, other providers, author-facing composition verbs.
