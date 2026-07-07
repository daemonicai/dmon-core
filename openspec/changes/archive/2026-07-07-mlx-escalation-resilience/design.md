## Context

The daemon (`daemon/Daemon.cs`) routes each turn through `TriageRouter` (`daemon/Daemon.Routing`), which resolves three backends (first-line, escalation, egress) from ADR-032 `Func<IServiceProvider, ValueTask<IChatClient>>` factory delegates. The first-line and escalation backends both resolve to `sp.MlxClient(key)` (`providers/Dmon.Providers.Mlx/MlxClientExtensions.cs`), which calls `MlxProviderExtension.EnsureRunningAsync` (spawn/attach an `mlx_lm.server` on a fixed port) and then builds an OpenAI-compatible `IChatClient`. `EscalationWarmingService` (`daemon/Daemon.Routing`) is an `ISessionActivityListener` that fire-and-forget warms the escalation runtime on activity and tears it down via `StopAsync` after an idle window (ADR-034).

Three defects (audit 2026-07-06 #2, re-verified on `main@b1a29f6`):

1. **`TriageRouter` caches each backend as `Lazy<Task<IChatClient>>` (`ExecutionAndPublication`).** The value factory (`() => factory(sp).AsTask()`) does not itself throw — the transient failure lives *inside* the returned `Task`. `Lazy` caches that faulted `Task` permanently, so a transient first-turn failure (model not yet downloaded, port busy) poisons the backend until process restart.
2. **After idle `StopAsync`, nothing on the request path respawns the escalation server.** The request path awaits the cached `IChatClient` and never re-invokes `EnsureRunningAsync`, so the `EscalationWarmingService` "self-heal path" comment is false; correctness currently rests on the next fire-and-forget warm racing ahead of the turn. Notably, the standing specs (`escalation-warming` "Warming is an optimization…", `triage-routing` "Warming integrates…") **already require** the request path to ensure-running — the code simply does not honor them.
3. **`EnsureRunningAsync` has no concurrency gate.** Two concurrent callers (racing `Lazy` first-turns, or a warm racing a turn) both observe not-running and both `SpawnServer(fixedPort)`; the second `_serverProcess = process` assignment orphans the first `mlx_lm` process (unreachable by `KillServer`/`Dispose`), plus concurrent `uv pip install` on one venv.

## Goals / Non-Goals

**Goals:**
- A transient backend-resolution fault is recoverable on a later turn; a success is still cached once and resolution stays lazy/I/O-free at construction (ADR-027 D1 / ADR-032 D3).
- A torn-down (or otherwise dead) escalation runtime is respawned on demand before the escalation client produces output — making the "self-heal" real and matching the existing `escalation-warming`/`triage-routing` specs.
- `EnsureRunningAsync` spawns exactly one server and provisions the venv once under concurrency, with no orphaned process.
- Regression tests for all three: fault-recovery, post-teardown respawn, concurrent single-spawn.

**Non-Goals:**
- No change to the wire protocol, RPC, session storage, gateway/network, or other providers.
- No change to author-facing composition verbs (`UseTriage`/`AddEscalation`/`AddEgress`/`AddMlx*`) signatures.
- No generalization of the concurrency guarantee onto the `IProviderExtension` (ADR-007) contract — kept MLX-local.
- No change to the warming service's own contract (warm-on-activity, idle-teardown) — it is unchanged; only the request-path self-heal it *relies on* is made real.
- No new ADR: the change conforms to ADR-007/032/034 rather than contradicting them (see Decisions D4).

## Decisions

### D1 — Replace the poisoning `Lazy<Task<IChatClient>>` with a fault-recovering async resolver
Introduce a small per-backend resolver (in `daemon/Daemon.Routing`) that holds the in-flight/completed `Task<IChatClient>` behind a `SemaphoreSlim(1,1)` (or equivalent double-checked guard). On await: if there is a cached **successfully-completed** task, return it; otherwise (null or **faulted**) start a fresh resolution under the gate and cache it. A faulted resolution is discarded rather than cached, so the next turn retries.

- **Why not "recreate the `Lazy` on fault":** possible, but re-assigning a `Lazy` field under races is itself a concurrency hazard; an explicit gated resolver makes the resolve-once-on-success + retry-on-fault + single-flight-under-concurrency semantics testable in one place.
- **Preserves** the existing `FactoryLazyResolutionTests` guarantees (no I/O at `Create`, resolve-once-on-success, concurrent-first-turns-resolve-once) — those tests should continue to pass, extended with a faulted-then-succeeds case.
- Applies uniformly to all three backends (first-line/escalation/egress); egress (a remote client) simply never faults transiently in practice but gets the same robustness for free.

### D2 — Realize request-path self-heal via an ensure-running delegating `IChatClient` in the MLX provider
Wrap the OpenAI-compatible client that `MlxClient(key)` builds in a thin `DelegatingChatClient` that calls `EnsureRunningAsync` (attach-first, now concurrency-safe per D3) before delegating `GetResponseAsync`/`GetStreamingResponseAsync`. Because attach-first is cheap when the server is healthy, the steady-state cost is one liveness check per turn; when the server was torn down, the call respawns it transparently.

- **Why the client wrapper, not router code:** it keeps `TriageRouter` fully provider-agnostic (it only ever sees an `IChatClient`), and it makes literally true the wording the `escalation-warming` spec/comment already use ("the escalation client itself calls `EnsureRunningAsync`"). The cached client the router holds becomes genuinely self-healing, so the `triage-routing` "cached escalation client survives a respawn" scenario holds without router changes.
- **Alternative considered — router calls an ensure-running delegate before dispatch:** more testable in `Daemon.Routing.Tests` with a fake, but pushes provider lifecycle knowledge into the router and requires threading an extra delegate through `UseTriage`/`AddEscalation`. Rejected to preserve router provider-agnosticism. (The `triage-routing` MODIFIED requirement is deliberately mechanism-neutral so either seam satisfies it; the worker MAY choose the delegate seam if it proves materially simpler to test, provided the scenarios pass.)
- First-line is wrapped too (it is also built by `MlxClient`), giving the same self-heal for free; first-line is not torn down by the warming service, so this is defense-in-depth.
- The existing eager `EnsureRunningAsync` at `MlxClient` build time is retained (preserves warm-at-build), and a fault there is now recoverable via D1.

### D3 — Serialize `EnsureRunningAsync` with a `SemaphoreSlim(1,1)`, double-checked inside the gate
Guard the check-then-spawn critical section of `MlxProviderExtension.EnsureRunningAsync` with an instance `SemaphoreSlim(1,1)`. Re-run the `IsRunningAsync` liveness check *inside* the gate so a caller that waited behind an in-progress spawn attaches to the resulting server instead of spawning again. `StopAsync` SHALL acquire the same gate so an idle teardown cannot interleave with a spawn (avoiding a kill-mid-spawn orphan). Dispose the semaphore in `Dispose`.

- Keeps the attach-first/idempotent contract; adds the concurrency guarantee the `mlx-provider` "Concurrent-safe start" ADDED requirement now states.
- `KillServer`'s existing `Interlocked.Exchange(ref _serverProcess, null)` stays (kill idempotency); the gate is about spawn exclusivity, orthogonal to it.

### D4 — Conform to ADR-007/032/034; no superseding ADR
- **ADR-032 D3 / ADR-027 D1 ("`Create` performs no I/O"):** preserved — the resolver stays lazy; construction invokes no factory.
- **ADR-034 D2 ("warming never blocks a session command or turn"):** the *background warming* remains fire-and-forget and unchanged. The escalation **turn** ensuring its own backend (D2) is not "warming" — it is the turn doing the minimum work required to reach a live model, which the existing `escalation-warming` spec already sanctions ("spawning synchronously on that path, incurring only added latency for that turn"). This is conformance, not contradiction. **If, during apply, the ensure-running-per-turn is judged to violate ADR-034 D2, that is a stop-and-ask** (a superseding ADR must be accepted first) — but the current reading is that it does not.

## Risks / Trade-offs

- **[Per-turn liveness check adds latency to every escalation turn]** → attach-first `IsRunningAsync` is a cheap local HTTP/health probe; steady-state cost is one round-trip against localhost. Acceptable versus the current failure mode (turn hits a dead endpoint). If measured cost is material, the wrapper can short-circuit on a recently-verified-live timestamp — deferred unless needed.
- **[`StopAsync` acquiring the spawn gate could briefly block an idle teardown behind an in-progress spawn]** → teardown only fires after an idle window with no activity, so contention is near-zero; correctness (no orphan) outranks the rare brief wait.
- **[Fault-recovering resolver could mask a *persistent* backend failure by retrying every turn]** → acceptable and desirable: each turn already needs the backend; retrying per-turn surfaces the same error to the caller each time (no worse than today) while allowing recovery once the transient cause clears. No retry storm — at most one resolution attempt per turn.
- **[Wrapping first-line per-turn changes its steady-state path]** → first-line is normally always resident, so the check attaches with no spawn; negligible.

## Open Questions

_None blocking._ The seam for D2 (client wrapper vs. router delegate) is left to the worker within the mechanism-neutral spec; the recommended choice is the client wrapper (D2).
