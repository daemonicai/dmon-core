# ADR-034: MLX Local Runtime

**Date:** 2026-06-29
**Status:** Accepted
**Amends:** ADR-007 (provider lifecycle — adds `StopAsync`), ADR-006 (provider-spawn confirmation gate — composition-declared backends carry standing consent), ADR-032 (escalation backend shape + bring-up home)
**Builds on:** ADR-007 (provider-extension lifecycle), ADR-027 (routing policy home), ADR-023 (granular provider packages), ADR-021 (composition-as-consent boundary), ADR-024 (protocol-cycle lockstep versioning)

## Context

The Daemon's routing (ADR-027/032) runs a first-line model (`gemma-4-e4b-it-qat-OptiQ-4bit`) that self-escalates to a larger model (`gemma-4-26B-A4B-it-qat-nvfp4`) via `think_harder`. Both must be locally resident simultaneously for the hand-off to be smooth. `mlx_lm.server` holds **one model per process**, so two resident models require two processes on two fixed ports. The shipped `Dmon.Providers.Omlx` drives the oMLX GUI app, which is also single-model — a hard blocker.

A spike (`docs/notes/mlx-runtime-exploration.md`, live-verified 2026-06-29) established the runtime facts:

- Stock `mlx_lm.server` ≥0.31.3 does gemma-4 tool calls end-to-end via its `gemma4` tool parser. No custom Python server is needed.
- `gemma-4-E4B-it-qat-nvfp4` is unusable for tool calling at that size (over-quantized — rambling, ad-hoc JSON, glyph corruption). `gemma-4-e4b-it-qat-OptiQ-4bit` is clean. The 26B tolerates nvfp4.
- `/v1/models` lists cached models, not resident ones — useless as a readiness signal.
- gemma-4 emits a separate `reasoning` field; `max_tokens` must be generous enough to clear reasoning before a tool call is reached.
- `uv` is the correct runtime prerequisite: it owns a pinned interpreter and pins `mlx_lm`, eliminating the system-Python dependency.

Three binding ADRs are touched by this change. This ADR records those amendments so they can be accepted as a gate before implementation begins (design.md D8).

## Decision

### 1. ADR-007 amendment: `StopAsync` is added to the provider lifecycle

`IProviderExtension` gains `StopAsync(CancellationToken cancellationToken)`, complementing the existing attach-first `EnsureRunningAsync`. A provider that **spawned and owns** a server process SHALL terminate that process and release its port on `StopAsync`.

This addition is **additive and non-breaking**: existing start-only and attach-only providers receive a **default no-op `StopAsync`** in the interface. A provider that attached to an externally-managed server leaves it running when `StopAsync` is called. All current providers (`Dmon.Providers.Mtplx`, `Dmon.Providers.LlamaCpp`, `Dmon.Providers.Ollama`, and all cloud providers) are unaffected — no change to their implementations, contracts, or tests.

`StopAsync` is the contract delta behind the idle-teardown half of the warming lifecycle (decisions 2 and 3 below).

### 2. ADR-006 amendment: composition-declared backends carry standing spawn consent

A local model runtime **declared as a backend in the composition root** carries the composition author's **standing consent** to be (re)spawned — on start, warm, or respawn after idle teardown — **without an interactive confirmation prompt on each call**.

This replaces ADR-007 Decision 2's per-`EnsureRunningAsync` `tool.confirmRequest risk:high` gate, **for composition-declared backends only**. The reasoning parallels ADR-021's composition-as-consent boundary: the user authored the `.cs` composition that declares the backend. That authorship act is the consent. Spawning a declared backend is not an agent-initiated `compose` reload; it is the standing declaration being executed.

The boundary is crisp: **interactive and ad-hoc provider use still requires the normal ADR-006 confirmation gate**. Nothing in ADR-006 is changed for that path. This amendment applies exclusively where the backend is a named, pinned declaration in the composition root.

This is the permission foundation that allows `EscalationWarmingService` to call `EnsureRunningAsync`/`StopAsync` repeatedly across sessions without prompting.

### 3. ADR-032 amendment: escalation backend is a fixed-port mlx runtime with activity-warming and idle-teardown

ADR-032 Decision 3 placed oMLX bring-up inside the lazy factory delegate. This ADR amends it: the escalation backend is the **mlx escalation runtime on a fixed port**, not oMLX, with activity-driven warming and idle-teardown. The lazy `EnsureRunningAsync` call inside the factory delegate is **retained** as the self-heal backstop (decision 3 detail below).

**Why a fixed port:** ADR-032's lazily-resolved escalation `IChatClient` may be cached for the daemon's lifetime. A fixed port lets that cached client transparently reconnect after a teardown→respawn cycle. A dynamic port would strand the cached client and require a new factory call.

**Warming is additive to routing, not a change to routing semantics.** Warming pre-starts the escalation runtime before a turn needs it. It does **not** change when escalation is decided or which backend handles a turn. The escalation request path **still calls `EnsureRunningAsync`** inside its lazy factory delegate (ADR-032 D3's pattern), so routing is correct whether or not warming completed. Warming removes hand-off latency; it is a best-effort optimisation with a self-heal backstop.

**The architectural split across three layers** keeps each ADR boundary intact:

- **Core (trigger):** a new in-process `ISessionActivityListener` interface (`OnSessionActivated(string id)`, `OnTurnStarted(string id)`), DI-discovered and invoked by `SessionHandler` (where sessions become active) and `TurnHandler` (the per-turn chokepoint). It carries **no policy** — it is a neutral signal that a session became active or a turn started. It is **not** on the RPC wire (honours ADR-003 and ADR-016: no new wire message type).

- **Daemon (policy):** `EscalationWarmingService : ISessionActivityListener` plus an idle timer lives in `daemon/Daemon.Routing`. On activity: fire-and-forget `EnsureRunningAsync(escalation)` and reset the timer. On idle expiry: `StopAsync(escalation)`. This is the correct home for routing-related policy per ADR-027 Decision 5: routing policy lives in the daemon, not in `middleware/` and not in a protocol-keyed package.

- **Provider (mechanism):** `EnsureRunningAsync` (existing) + `StopAsync` (new, Decision 1). The provider knows nothing of sessions — it only manages its own server process.

The idle window is configurable with a sane default (~10 minutes). The exact default is an Open Question.

## Consequences

- `Dmon.Providers.Omlx` is removed. This is a clean break (no production deployments) — the only consumer is the Daemon composition, which switches to the new `Dmon.Providers.Mlx` verbs in the same change.
- Existing providers gain a default no-op `StopAsync`. No existing provider implementation changes.
- The `IProviderExtension` interface acquires one new member. Because it ships with a default no-op implementation, existing provider implementations remain source-compatible and need no edits; the protocol-cycle lockstep version bump (ADR-024) re-releases the set regardless.
- `EscalationWarmingService` can warm and tear down the escalation runtime repeatedly without user prompts, enabled by Decision 2's composition-declared consent.
- If warming completes before a turn escalates, the escalation turn has no cold-start cost. If it has not, the escalation path's own `EnsureRunningAsync` spawns synchronously — correct, but one turn pays the start-up cost. Best-effort is the right trade-off given the self-heal backstop.
- The first-line model (`gemma-4-e4b-it-qat-OptiQ-4bit`, small) stays resident permanently, holding a modest memory footprint. The 26B escalation runtime is only resident during active use, limiting memory pressure on 36–48 GB machines.

## Alternatives

- **Custom multi-model single-port server.** Rejected: more owned code, GIL contention between concurrent model calls, and the spike proved it unnecessary — stock `mlx_lm.server` does the job cleanly.
- **Provider subscribes to session events directly.** Rejected: this leaks session lifecycle and escalation policy into a provider-agnostic mechanism. The provider should know nothing of sessions; that knowledge belongs in the daemon.
- **Refcount on session existence.** Rejected: sessions are resumable and never cleanly "end" (there is no session-ended event). An idle timer keyed on turn activity is the correct frame; the timer is the refcount-with-grace.
- **Overload `Dmon.Providers.Omlx` rather than creating a new provider.** Rejected: Omlx *is* the GUI-app launcher (a single-process, single-model, user-visible app). Replacing its spawn model to headlessly manage two processes would be a category error. A clean new provider is the right boundary.
- **Dynamic port for the escalation runtime.** Rejected: a dynamic port strands the cached `IChatClient` resolved by ADR-032's lazy factory after a teardown→respawn cycle. Fixed port is required for transparent reconnect semantics.

## Open Questions

These do not block acceptance of this ADR; they are resolved during implementation:

1. **Exact idle-timeout default** — the design leans toward ~10 minutes, configurable from the start. The precise value and whether a `DMON_ESCALATION_IDLE_TIMEOUT` env override is exposed at first cut is left to the implementer.
2. **Readiness probe: `/health` vs 1-token completion** — the spike found `/v1/models` useless (lists cached, not resident, models). Whether `mlx_lm.server`'s `/health` endpoint reflects model-load state must be verified during implementation; if not, the fallback is a 1-token completion probe (matching the existing provider patterns).
3. **`reasoning` field mapping** — gemma-4 returns a separate `reasoning` field alongside `content`. Whether this surfaces as reasoning content through the M.E.AI layer or is discarded needs resolution when the provider client is wired.

## Relationship to other ADRs

- **ADR-006** — amended: the spawn-confirmation gate (per-`EnsureRunningAsync` `tool.confirmRequest risk:high`) is replaced by standing consent for composition-declared backends. Interactive/ad-hoc use retains the gate unchanged.
- **ADR-007** — amended: `StopAsync(CancellationToken)` is added to `IProviderExtension` with a default no-op (Decision 1); separately, Decision 2 narrows ADR-007 Decision 2's per-`EnsureRunningAsync` spawn-confirmation gate for composition-declared backends. All existing providers are unaffected.
- **ADR-032** — amended: Decision 3's escalation backend changes from oMLX (inside the lazy factory delegate) to the fixed-port mlx escalation runtime with activity-warming + idle-teardown; the lazy `EnsureRunningAsync` self-heal inside the delegate is retained.
- **ADR-027 D5** — unchanged: routing policy lives in `daemon/Daemon.Routing`, not `middleware/`, and not a protocol-keyed package. `EscalationWarmingService` honours this — it is a daemon service, not middleware.
- **ADR-019** — unchanged: `DmonHostBuilder.Build()` terminal-client selection (the `ITerminalClientFactory` precedence rule) is unmodified. The hosting surface and `RunAsync` loop are unaffected.
- **ADR-023** — the new `Dmon.Providers.Mlx` is a granular, protocol-line provider package following the pattern established for `Dmon.Providers.Mtplx` and `Dmon.Providers.LlamaCpp`. `Dmon.Providers.Omlx` is removed from the protocol-lockstep set.
- **ADR-021** — built on: Decision 2's standing-consent reasoning reuses ADR-021's composition-as-consent boundary. Composition-declared backend spawn is the author's standing declaration being executed, not an agent-initiated `compose` reload, so it does not engage the `compose` permission tier.
- **ADR-024** — built on: the `IProviderExtension` contract delta (Decision 1) rides the protocol-cycle lockstep re-release; no independent break is introduced.
