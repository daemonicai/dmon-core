## Context

The Daemon's routing (ADR-027/032) runs a first-line model that self-escalates to a larger model via `think_harder`. Both must be local and resident simultaneously for the hand-off to be smooth. The shipped `Dmon.Providers.Omlx` drives the oMLX GUI app, which holds one model at a time — a hard blocker.

A spike (`docs/notes/mlx-runtime-exploration.md`, live-verified 2026-06-29) established the runtime facts so the design rests on evidence, not assumption:

- Stock `mlx_lm.server` (≥0.31.3) does gemma-4 tool calls **correctly end-to-end** through its `gemma4` tool parser. No custom Python server is needed.
- `mlx_lm.server` holds **one model per process**; two resident models ⇒ two processes on two ports.
- **`gemma-4-E4B-it-qat-nvfp4` is unusable for tool calling** (nvfp4 over-quantizes the small model → rambling, ad-hoc JSON, glyph corruption). **`gemma-4-e4b-it-qat-OptiQ-4bit` is clean.** The 26B tolerates nvfp4.
- `/v1/models` lists *cached*, not *resident*, models — useless as a readiness signal.
- gemma-4 is a reasoning model: `mlx_lm.server` returns a separate `reasoning` field; `max_tokens` must be generous or the tool call is never reached.

The system's Python is unreliable (homebrew 3.14 cannot import `mlx_lm`), so the design does not depend on system Python.

This change is scoped to the runtime + the warming lifecycle. Model-download orchestration, `daemon-scheduler`, and extracting the shared local-runtime helper are explicitly out of scope.

## Goals / Non-Goals

**Goals:**
- A headless local runtime that keeps the first-line model permanently hot and an escalation model warm during active use, on separate fixed ports.
- Reliable gemma-4 tool calling via stock `mlx_lm.server`, with a deterministic, version-pinned Python environment via `uv`.
- A clean three-layer collaboration for the escalation model's memory lifecycle (idle-timeout, ref-counted) that respects existing ADR boundaries.
- A neutral, reusable core seam for session/turn activity (not mlx-specific).

**Non-Goals:**
- Custom OpenAI-compatible server code (the spike removed it).
- Model-download UX / orchestration (rely on `mlx_lm` first-run download).
- Multi-model-per-process or dynamic model swapping within one server.
- Folding in `daemon-scheduler`; extracting the shared spawn/poll/probe helper.
- Keeping `Dmon.Providers.Omlx` — it is removed (clean break).

## Decisions

### D1 — Two processes, two fixed ports; one `mlx_lm.server` per model
Forced by `mlx_lm.server`'s one-model-per-process design. The first-line and escalation runtimes are independent processes the provider owns by real PID (the Mtplx/LlamaCpp `_serverProcess` pattern), **not** the oMLX single-app model. *Alternative rejected:* a custom multi-model server (one port, both resident) — more code we own, loses independent lifecycle, GIL contention; the spike proved it unnecessary.

**Escalation uses a FIXED port** (not LlamaCpp-style dynamic). ADR-032's escalation backend may cache an `IChatClient` bound to that port; teardown→respawn on the *same* port lets the cached client transparently reconnect after an idle cycle. A dynamic port would strand the cached client.

### D2 — `uv` as the runtime prerequisite (managed venv, real PID)
`uv` owns a pinned interpreter and pins `mlx_lm`, eliminating the system-Python dependency and satisfying the load-bearing version pin in one stroke. **A managed venv** (build once, then spawn `<venv>/bin/python -m mlx_lm.server …`) is chosen over `uv run --with`: the provider must own the **real** server PID for clean teardown (matching the existing local providers); a `uv run` wrapper interposes a process and complicates tree-teardown. `IsApplicable()` = arm64/macOS + `uv` on PATH (cheap, no I/O — ADR-007). Env build + spawn happen in `EnsureRunningAsync()`.

### D3 — Model pairing pinned by quant evidence
First-line = `gemma-4-e4b-it-qat-OptiQ-4bit`; escalation = `gemma-4-26B-A4B-it-qat-nvfp4`. Defaults baked into the provider's config but overridable. nvfp4 is explicitly disallowed as a default for the small first-line model.

### D4 — Readiness probe is a tiny completion / `/health`, not `/v1/models`
`/v1/models` returns cached models regardless of what is loaded. Readiness must confirm the *resident* model answers — a 1-token completion (or `/health` if it reflects load state). The existing tool-calling probe still runs once to set capability state.

### D5 — Three-layer lifecycle: neutral core seam + daemon policy + provider mechanism
The escalation model's idle-timeout/ref-counted lifecycle is split so each layer keeps its concern:

- **Core (trigger):** a new in-process `ISessionActivityListener { OnSessionActivated(id); OnTurnStarted(id); }`, DI-discovered, invoked by `SessionHandler` (where `SessionCreated`/`SessionLoaded` are already emitted) and `TurnHandler` (the per-turn chokepoint). It carries **no policy** — purely "a session became active / a turn started." It is **not** on the RPC wire.
- **Daemon (policy):** `EscalationWarmingService : ISessionActivityListener` plus an idle timer. On activity: fire-and-forget `EnsureRunningAsync(escalation)` and reset the timer. On idle expiry: `StopAsync(escalation)`. This is the routing-policy home (ADR-027), not core, not a protocol-keyed package.
- **Provider (mechanism):** `EnsureRunningAsync` (attach-first, exists) + new `StopAsync` (kills the escalation `_serverProcess`). Mechanism only; knows nothing of sessions.

*Alternative rejected:* provider subscribes to session events directly — leaks session/escalation policy into a provider-agnostic mechanism. *Alternative rejected:* refcount on session existence — sessions are resumable and never cleanly "end" (no "session ended" event exists), so activity-keying with an idle timer is the correct frame; the timer is the refcount-with-grace.

### D6 — Warming is a best-effort optimization with a self-heal backstop
The escalation path already calls `EnsureRunningAsync` inside its lazy `Func` (verified). So if warming missed (user escalates faster than warmup, or just after an idle teardown), the escalation path spawns synchronously — correct, just slower that one turn. The warming service therefore never blocks a turn and need not be perfectly timed.

### D7 — Replace Omlx with a new `Dmon.Providers.Mlx` (no overload, no shared-helper extraction)
A new provider rather than overloading Omlx (Omlx *is* the GUI-app launcher). `Dmon.Providers.Omlx` and its spec are removed. The new provider reuses — by copy, for this change — the Mtplx/LlamaCpp spawn/poll/probe pattern; extracting a shared helper (the third clone) is deferred to keep scope tight.

### D8 — ADR-034 amends ADR-006/007/032; accepted before code
Binding ADRs are touched: ADR-007 (add `StopAsync`), ADR-006 (composition-declared backends carry standing spawn consent — the user authored the composition that declares the backend, so no per-warm/respawn prompt; interactive use keeps the gate), ADR-032 (escalation = mlx fixed-port runtime with activity-warming + idle-teardown). Per the project's ADR rules these are captured in a single new **ADR-034** that must be **accepted before** implementation begins.

## Risks / Trade-offs

- **uv first-run resolve latency / offline** → First env build downloads `mlx_lm`; subsequent runs are cached (~167 ms warm). Mitigate by building the env at first applicability and surfacing a clear message; document that first run needs network.
- **Escalation respawn races the cached client** → Fixed port (D1) lets the cached `IChatClient` reconnect after respawn; the escalation path's own `EnsureRunningAsync` (D6) guarantees correctness if a request arrives mid-respawn.
- **Idle teardown thrash under bursty use** → Idle grace window (configurable N minutes) plus warm-on-any-turn smooths it; not strict session-scoped (which would thrash).
- **`StopAsync` added to the lifecycle contract may affect other providers** → It is additive; existing start-only providers get a default no-op `StopAsync` (attach-only semantics) so nothing regresses. (ADR-034 specifies this.)
- **Memory pressure (~19 GB both-resident) on 36–48 GB targets** → Exactly why the escalation model is idle-released; first-line stays small (OptiQ-4bit).
- **`reasoning` field handling** → The provider/agent mapping must surface or discard the `reasoning` field without breaking content/tool_calls parsing; `max_tokens` defaults must be generous enough to clear reasoning before the tool call.
- **Removing Omlx is BREAKING** → Acceptable: no production deployments (clean-break policy); the only consumer is the Daemon composition, switched in the same change.

## Migration Plan

1. Accept **ADR-034** (amends ADR-006/007/032). *Gate — no code before this.*
2. Land the core `ISessionActivityListener` seam + invocations (no behavior change on its own).
3. Land `Dmon.Providers.Mlx` (uv env, two keyed runtimes, `EnsureRunningAsync`/`StopAsync`, completion/`/health` readiness, tool-calling probe).
4. Land the daemon `EscalationWarmingService` and switch `Daemon.cs` from `UseOmlx` to the mlx verbs.
5. Remove `Dmon.Providers.Omlx` + `omlx-provider` spec.
6. Update daemon deploy/setup docs (uv prerequisite, model pairing).

Rollback: the change is pre-deployment; revert the branch. No data migration.

## Open Questions

- Exact idle-timeout default (N minutes) and whether it is config-exposed at first cut. *(Lean: configurable, sane default ~10 min.)*
- Whether the readiness probe prefers `/health` or a 1-token completion (depends on whether `mlx_lm.server`'s `/health` reflects model-load state — verify during implementation).
- How the `reasoning` field maps through the M.E.AI layer (surface as reasoning content vs. drop) — resolve when wiring the provider client.
