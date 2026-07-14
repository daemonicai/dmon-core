# DEVLOG — rpc-failure-resilience

Architect split this change into 3 impl blocks (one per finding) + per-block tests + final gates:
- **Block 1 = #12** (tasks 1.1–1.3, 4.1) — RpcClient faults pending on pump exit. **DONE.**
- **Block 2 = #13** (tasks 2.1, 2.2, 4.2) — CoreProcessManager awaits exit after Kill. **DONE.**
- **Block 3 = #8** (tasks 3.1–3.2, 4.3) — Desktop async-void handler guards.
- Gates 5.1–5.3 at the end.

## NEXT
- **Block 3 = #8** (tasks 3.1–3.2, 4.3): wrap `frontends/Dmon.Desktop/SessionViewModel.HandleToolConfirmAsync`/`HandleUiInputAsync` bodies in try/catch (no escape from `async void`), surface an error state (find the log/diagnostic surface); happy path byte-for-byte identical; handlers independent. Test via the `FakeCoreSession` seam throwing from `SendAsync`, dispatch `ToolConfirmRequestEvent` + `UiInputRequestEvent`, assert no exception escapes. Then gates 5.1–5.3 → change complete.

## Block 1 — #12 RpcClient faults pending on pump exit — DONE (tasks 1.1–1.3, 4.1)

`RunPumpAsync` now faults in-flight `RequestAsync` promptly when the core exits or the pump faults, instead of hanging to the 30s request timeout.

- **New `core/Dmon.Runtime/RpcTransportClosedException.cs`** (`sealed`, `CommandId`, optional inner cause) — distinct from `OperationCanceledException` and `RpcTimeoutException` so callers tell "channel gone" (non-retryable) from "slow" (timeout).
- **Three-exit model (design D1):** (a) disposal-cancellation → leave `_pending` for `DisposeAsync` (`ObjectDisposedException`, unchanged); (b) EOF → fault pending with `RpcTransportClosedException(id, null)`; (c) non-OCE pump fault → CAUGHT inside `RunPumpAsync` (so it can't resurface unobserved at `DisposeAsync`'s `await _pumpTask`), faults pending with the cause. Each pending drained exactly once via `TryRemove`+`TrySetException` (no double-completion vs a concurrently-landing correlated result).
- **Review refinement (folded in):** guarded the OCE catch with `when (cancellationToken.IsCancellationRequested)` — a **stray** OCE (e.g. `TaskCanceledException` from a dying transport read, disposal NOT requested) now falls through to the fault path instead of being misclassified as cancellation and hanging. This makes the code *match* the spec ("fault ... for any reason other than caller-initiated cancellation") — it was a correctness gap, not a scope expansion.
- **ADR-015 untouched** — fault path beneath the wire contract; no protocol/type/`IRpcClient`/`IRpcTransport` signature change.
- **Tests (3):** EOF→prompt transport-closed (not timeout); non-OCE fault→transport-closed carrying the cause + clean disposal (proves observed); stray-OCE→transport-closed (not hang). `FeedableTransport.Fault(ex)` = `Writer.Complete(ex)` seam added.

**Reviewer:** Approve with nits; verified the three-exit model, double-completion safety, observability. Nits closed: OCE guard (above), `await using` in the fault test. (Untracked-file nit handled by orchestrator at commit.)

**Gates:** `make build` 0-warn; full `make test` 20/20 projects green (Runtime 43/43); `openspec validate --strict` valid.

## Block 2 — #13 CoreProcessManager awaits exit after Kill — DONE (tasks 2.1, 2.2, 4.2)

`StopAsync`'s forced-kill path now awaits the process exit so the OS releases the session-dir `.lock` before the method returns, closing the `SessionLockedException` respawn race.

- **`core/Dmon.Runtime/CoreProcessManager.cs`:** in the graceful-timeout OCE catch, after `Kill(entireProcessTree:true)`, `await WaitForExitAsync` under a **fresh bounded** `CancellationTokenSource(5s)` (NOT the already-fired 500ms `cts`, NOT `None`), wrapped in its own best-effort try/catch so a second timeout / already-exited process can't throw or hang `StopAsync` (spec: bounded wait elapses → still returns). Graceful path / `RestartAsync` / signatures untouched. ADR-004 host-side lock honouring; no protocol change.
- **Deterministic test needed a stub** — the REAL core exits on stdin EOF so the graceful wait always succeeds and the kill path never runs. New fixture **`test/fixtures/Dmon.StubCore`** (Exe, dependency-free, TWE): takes `<cwd>/.lock` (`FileShare.None`, matching `SessionLock`), emits `agentReady` AFTER locking (load-bearing handshake), then `Thread.Sleep(Infinite)` ignoring stdin (`GC.KeepAlive(lockFile)` after the sleep keeps the FileStream alive in Release/TWE). Referenced via `ProjectReference` (dll copied beside the test). `CoreProcessManagerStopTests` starts it via `LaunchMode.DotnetExec`, waits `agentReady`, `StopAsync`, then asserts `!IsRunning` AND an exclusive `.lock` reacquire succeeds without `IOException` (spec-faithful proxy for "replacement acquires lock without SessionLockedException"). Genuinely fails on reverted code.

**Reviewer:** Approve, no blockers. Verified the fresh-bounded-token fix, best-effort guard totality, and that the test truly fails pre-fix (the `.lock` reacquire is the real guard — deterministic on Windows, kernel-teardown-released on Unix; post-fix green on all). Accepted cosmetic nits (redundant `using`s under ImplicitUsings; "replicated flags" comment is really the correct subset; 5s budget larger than "short" but a fine safety ceiling) — build-clean, not worth a round-trip.

**Gates:** `make build` 0-warn; full `make test` 20/20 projects green (Runtime 44/44); `openspec validate --strict` valid.
