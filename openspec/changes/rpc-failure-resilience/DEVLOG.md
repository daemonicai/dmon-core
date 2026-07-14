# DEVLOG — rpc-failure-resilience

Architect split this change into 3 impl blocks (one per finding) + per-block tests + final gates:
- **Block 1 = #12** (tasks 1.1–1.3, 4.1) — RpcClient faults pending on pump exit. **DONE.**
- **Block 2 = #13** (tasks 2.1, 4.2) — CoreProcessManager awaits exit after Kill. NEXT.
- **Block 3 = #8** (tasks 3.1–3.2, 4.3) — Desktop async-void handler guards.
- Gates 5.1–5.3 at the end.

## NEXT
- **Block 2 = #13** (tasks 2.1, 4.2). `core/Dmon.Runtime/CoreProcessManager.cs`: after `_process.Kill(entireProcessTree:true)` on the graceful-timeout catch (~line 112), `await WaitForExitAsync` with a fresh **bounded** token so the OS releases the session-dir lock before `StopAsync` returns; guard it (best-effort, like the existing `catch {}` around Kill) so a second timeout / already-exited process can't throw out of `StopAsync`. Leave the graceful non-kill path (already awaits exit) unchanged. Test 4.2 needs a short-lived core-launch harness that forces the graceful-shutdown timeout and asserts no `SessionLockedException` on respawn — heavier than block 1's channel test, so its own block.
- **Block 3 = #8** (tasks 3.1–3.2, 4.3): wrap `SessionViewModel.HandleToolConfirmAsync`/`HandleUiInputAsync` bodies in try/catch (no escape from `async void`), surface an error state; test via the `FakeCoreSession` seam throwing from `SendAsync`.

## Block 1 — #12 RpcClient faults pending on pump exit — DONE (tasks 1.1–1.3, 4.1)

`RunPumpAsync` now faults in-flight `RequestAsync` promptly when the core exits or the pump faults, instead of hanging to the 30s request timeout.

- **New `core/Dmon.Runtime/RpcTransportClosedException.cs`** (`sealed`, `CommandId`, optional inner cause) — distinct from `OperationCanceledException` and `RpcTimeoutException` so callers tell "channel gone" (non-retryable) from "slow" (timeout).
- **Three-exit model (design D1):** (a) disposal-cancellation → leave `_pending` for `DisposeAsync` (`ObjectDisposedException`, unchanged); (b) EOF → fault pending with `RpcTransportClosedException(id, null)`; (c) non-OCE pump fault → CAUGHT inside `RunPumpAsync` (so it can't resurface unobserved at `DisposeAsync`'s `await _pumpTask`), faults pending with the cause. Each pending drained exactly once via `TryRemove`+`TrySetException` (no double-completion vs a concurrently-landing correlated result).
- **Review refinement (folded in):** guarded the OCE catch with `when (cancellationToken.IsCancellationRequested)` — a **stray** OCE (e.g. `TaskCanceledException` from a dying transport read, disposal NOT requested) now falls through to the fault path instead of being misclassified as cancellation and hanging. This makes the code *match* the spec ("fault ... for any reason other than caller-initiated cancellation") — it was a correctness gap, not a scope expansion.
- **ADR-015 untouched** — fault path beneath the wire contract; no protocol/type/`IRpcClient`/`IRpcTransport` signature change.
- **Tests (3):** EOF→prompt transport-closed (not timeout); non-OCE fault→transport-closed carrying the cause + clean disposal (proves observed); stray-OCE→transport-closed (not hang). `FeedableTransport.Fault(ex)` = `Writer.Complete(ex)` seam added.

**Reviewer:** Approve with nits; verified the three-exit model, double-completion safety, observability. Nits closed: OCE guard (above), `await using` in the fault test. (Untracked-file nit handled by orchestrator at commit.)

**Gates:** `make build` 0-warn; full `make test` 20/20 projects green (Runtime 43/43); `openspec validate --strict` valid.
