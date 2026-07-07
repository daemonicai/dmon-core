# DEVLOG — mlx-escalation-resilience

Pinned facts (read before planning any block):
- Branch `change/mlx-escalation-resilience`, base `main`. Gates: `make build` (0-warn, TWAE on), `env -u MEKO_API_KEY make test` single-run, `openspec validate mlx-escalation-resilience --strict`. `dotnet build daemon/Daemon.cs -c Release` is a final-block gate (task 4.1) — only blocks that touch `daemon/` need it re-run.
- Three findings, three blocks + gates: Group 1 (MLX concurrency-safe start, 1.x) → Group 2 (MLX request-path self-heal wrapper, 2.x) → Group 3 (TriageRouter fault-recovering resolver, 3.x) → Group 4 (gates/spec alignment, 4.x). Group 2 and 3 both depend on Group 1's now-concurrency-safe `EnsureRunningAsync`.

## Block 1 — tasks 1.1–1.3 (DONE, committed)

**Deliverable:** Serialized, idempotent `MlxProviderExtension.EnsureRunningAsync` via instance `SemaphoreSlim(1,1) _gate`.

**Decisions made / how it was done:**
- `EnsureRunningAsync` acquires `_gate` (with `cancellationToken`) *before* the `try`; releases in `finally`. Re-runs `IsRunningAsync` liveness check **inside** the gate, **before** provisioning — a queued caller that finds the server already running attaches and skips provisioning, giving both "spawn once" and "provision at-most-once" for racing cold-start callers. Provisioning order otherwise unchanged (so version-pin-below-throws-before-spawn and cold-start probe-sequencing tests stayed green).
- Removed the unconditional `_runtimeState.OwnsProcess = false` on the attach branch. `OwnsProcess` defaults to `false` and is only set `true` immediately before this instance spawns; a queued caller re-checking liveness must not downgrade ownership (that would orphan the process from `StopAsync`/`Dispose`'s view). Legitimate attach-to-external-server still leaves it false. Reviewer confirmed against `AttachPath_...OwnsProcessFalse` / `Dispose_DoesNotKillAttachedProcess`.
- `StopAsync` is now `async Task` (signature unchanged), acquires the same gate, and catches `ObjectDisposedException` around `WaitAsync`/`Release` (checks `_disposed`) to survive a race with a concurrent `Dispose`. `OperationCanceledException` still propagates.
- `Dispose` disposes `_gate` without waiting (sync Dispose must not block); `KillServer`'s `Interlocked.Exchange(ref _serverProcess, null)` keeps the terminal kill race-safe.
- Test: new `EnsureRunningAsyncConcurrencyTests` class using the internal full-lifecycle test constructor. Test 1 blocks the first caller *inside* the critical section on a `TaskCompletionSource`, launches 2 more, asserts exactly 1 spawn + 1 provision + `OwnsProcess` true + `StopAsync` kills the one tracked process (SkippableFact, `/bin/sleep` dummy process, macOS-guarded). Test 2 = 8-caller smoke (no real process).

**Gates:** make build 0-warn; MLX project 73/73; full make test green (Core 606/607, all projects); validate --strict valid. Reviewer: **Approve**, no blockers.

**Carry-overs for later blocks:**
- **Architectural note for Block 2 (from reviewer):** `RunToolCallingProbeAsync` runs on the **attach** branch of `EnsureRunningAsync`. Pre-existing (not introduced here). But once Block 2's per-turn ensure-running wrapper (design D2) calls `EnsureRunningAsync` on every escalation turn, each turn incurs a tool-calling LLM round-trip on attach — the per-turn liveness cost the design flags may be larger than a single localhost round-trip. Weigh this when wiring the D2 wrapper; it may warrant a cheaper liveness check on the request-path hot path. Not a Block-1 defect.
- Nit (cosmetic, no action): `_disposed` is a non-`volatile` bool read across threads; real safety rests on `Interlocked.Exchange` + ODE catches. Field was already non-volatile pre-change.

## Block 2 — tasks 2.1–2.3 (DONE, committed)

**Deliverable:** MLX request-path self-heal wrapper.

**Decisions made / how it was done:**
- New `providers/Dmon.Providers.Mlx/EnsureRunningChatClient.cs` — `internal sealed : DelegatingChatClient`. Both `GetResponseAsync` and `GetStreamingResponseAsync` `await ext.EnsureRunningAsync(cancellationToken)` before delegating to `base`. Streaming is an `async IAsyncEnumerable<ChatResponseUpdate>` iterator that awaits ensure-running **before** the first `yield` (respawn strictly precedes dispatch even under deferred-iterator execution); `[EnumeratorCancellation]` on the token. No gating (relies on Block 1's `_gate`), no fault swallowing (a fault propagates uninterpreted — Block 3's router resolver depends on this).
- `MlxClientExtensions.cs` — wraps the inner decorator stack (`MlxMaxTokensDefaulter` → `CapabilitiesDecorator`) in `EnsureRunningChatClient` before returning; the wrapper is outermost. Eager build-time `EnsureRunningAsync` (line ~31) untouched. Applies to BOTH first-line and escalation runtimes (both go through `MlxClient`). Disposal chain is single-owned (`DelegatingChatClient.Dispose` chains to inner); wrapper does not dispose the extension (keyed DI singleton, shared across turns).
- Test: new `EnsureRunningChatClientTests.cs` — 4 tests: respawn-before-dispatch (asserts shared `callOrder == ["spawn","dispatch"]`) for non-streaming AND streaming; attach-without-spawn when already running (`startServerDelegate` never invoked); fault-propagates-and-never-dispatches (`TimeoutException` through, no dispatch).

**Gates:** make build 0-warn; MLX 77/77; full make test green (Core 606, Daemon.Routing 44 untouched); validate --strict valid. Reviewer: **Approve**, no blockers.

**CANDIDATE FOLLOW-UP (out of scope, tracked here — decided NOT to expand this change):** `EnsureRunningChatClient` calls `EnsureRunningAsync` on **every** turn for both runtimes. On the attach branch this runs the liveness completion AND `RunToolCallingProbeAsync` — a full tool-calling LLM round-trip (up to 2048 output tokens) — **per turn**, not just at cold start. Design D2 explicitly DEFERS the "recently-verified-live short-circuit" ("unless needed"), so shipping as-is is spec-conformant, but per-turn steady-state cost is real. Candidate future change: add a cheap-liveness short-circuit (e.g. last-verified timestamp) on the wrapper's hot path so already-verified-live turns skip the full probe. Reviewer concurred it's a mention-not-a-bug. Both required spec scenarios (respawn-after-teardown, attach-adds-no-spawn-latency) are met and tested.

## Block 3 — tasks 3.1–3.3 (DONE, committed)

**Deliverable:** TriageRouter fault-recovering backend resolver.

**Decisions made / how it was done:**
- `TriageRouter.cs` — replaced the three `Lazy<Task<IChatClient>>` fields with three instances of a new private nested `BackendResolver` class. `ResolveAsync(CancellationToken)`: lock-free fast-path read of a `volatile Task<IChatClient>? _cached` — returns ONLY when `IsCompletedSuccessfully`; otherwise `WaitAsync(cancellationToken)` (outside the try, so cancel-while-waiting never over-releases), double-checks the cache under the gate, invokes the factory, caches the task, awaits it under the gate (single-flight), and on fault sets `_cached = null` before rethrowing so the next turn retries. All six call sites (`ClassifyAsync`, both `GetResponseAsync`/`GetStreamingResponseAsync` × egress/first-line/escalation) call `.ResolveAsync(cancellationToken)`. `Dispose(bool)` calls `BackendResolver.DisposeResolved()` on each — disposes the client only if `IsCompletedSuccessfully`, disposes the gate, never force-resolves. Class `<remarks>` + ctor param docs updated to the fault-recovering semantics. NO ctor/verb/`ITerminalClientFactory` signature changed.
- `EscalationWarmingService.cs` — COMMENT-ONLY: reconciled the `<remarks>` "self-heal path" to describe the now-real request-path respawn realized by Block 2's `EnsureRunningChatClient` (calls `EnsureRunningAsync` before every dispatch). No logic change.
- Test: `FaultedResolution_IsNotCached_RecoversOnLaterTurn` in `FactoryLazyResolutionTests` — egress factory throws on first invocation, succeeds after; first `GetResponseAsync` throws `InvalidOperationException`, second succeeds and dispatches once (`egressResolveCount == 2`, `egressSpy.CallCount == 1`). Reviewer confirmed this WOULD FAIL against the old poisoning `Lazy` (which caches the factory exception and re-throws forever).

**Gates:** make build 0-warn; Daemon.Routing 45/45; full make test green (Mlx 77, Core 606); validate --strict valid; `dotnet build daemon/Daemon.cs -c Release` compiles clean. Reviewer: **Approve**, no blockers. Interleavings walked: faulted task never escapes fast path; cache-clear-on-fault races benign; single-flight preserved; cancellation clean; `volatile` is the correct model.

**Reviewer notes (non-blocking, pre-existing — not regressions):**
- Optional test-coverage gap: concurrent-fault and mid-resolution-cancellation cases are reasoned-correct but not directly asserted.
- Dispose-during-in-flight-resolve: `DisposeResolved()` disposes the gate with no sync against a live `ResolveAsync`, so an overlapping teardown+turn could throw `ObjectDisposedException` in the resolver's `finally` Release. Same no-turn-in-flight-at-teardown assumption the old `Lazy` design relied on — introduced-here only because the gate is now disposable, but not reachable today. Mental note if concurrent dispose/turn ever becomes reachable.

## Block 4 — tasks 4.1–4.3 (DONE, committed)

Verification-only block (no feature code). Gate results, freshly run:
- **4.1** `make build` clean (0-warn, TWAE on); `dotnet build daemon/Daemon.cs -c Release` compiles clean.
- **4.2** Targeted `test/Dmon.Providers.Mlx.Tests` (77/77) + `test/Daemon.Routing.Tests` (45/45) green; single full `env -u MEKO_API_KEY make test` green across every project (Core 606/607 w/ 1 expected skip, Terminal 187, Network 209, etc.).
- **4.3** `openspec validate mlx-escalation-resilience --strict` valid. Delta-spec-match confirmed by orchestrator against shipped code:
  - `mlx-provider` ADDED "Concurrent-safe start" (3 scenarios) ← Block 1.
  - `triage-routing` ADDED "Backend resolution recovers from a transient fault" (3 scenarios) ← Block 3.
  - `triage-routing` MODIFIED "Escalation backend is a fixed-port mlx runtime" (request-path ensure-running, attach-first, seam=client wrapper; 3 scenarios incl. respawn-after-teardown + no-respawn-latency) ← Block 2.

**Change complete: 12/12 tasks. Commits: 245c591 (block 1) → d26d443 (block 2) → a6c930e (block 3) → block 4.** Ready for user push/PR, then `/opsx:archive` proposal.
