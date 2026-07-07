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
