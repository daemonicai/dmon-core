## Context

Three host-side components each turn a dead/crashing core into a hang or a crash rather than a fast, clean failure.

- **`RpcClient` (`core/Dmon.Runtime/RpcClient.cs`).** The background pump `RunPumpAsync` enumerates `IRpcTransport.Events` once (design D2 of the original client). Its `try` catches only `OperationCanceledException` (normal cancellation-driven shutdown); its `finally` completes broadcast subscribers but never touches `_pending`. Pending `RequestAsync<TResult>` `TaskCompletionSource`s are faulted **only** in `DisposeAsync` (with `ObjectDisposedException`). So when the pump exits by stdout EOF (the child process died) or by a non-OCE exception, in-flight `RequestAsync` calls keep waiting on `tcs.Task` until the per-request timeout fires (`DefaultRequestTimeout` = 30 s). Worse, a non-OCE pump exception propagates out of `RunPumpAsync`, and `DisposeAsync`'s `await _pumpTask` catches **only** `OperationCanceledException`, so that fault would resurface at disposal.

  The existing `RpcTimeoutException` (`core/Dmon.Runtime/RpcTimeoutException.cs`) is the template for a timeout-distinguishable exception: a `sealed` `Exception` carrying `CommandId`, thrown from the request path, deliberately distinct from `OperationCanceledException`. The test seam already supports this scenario: `FeedableTransport` in `test/Dmon.Runtime.Tests/RpcClientTests.cs` has a `Complete()` method that signals end-of-stream.

- **`CoreProcessManager` (`core/Dmon.Runtime/CoreProcessManager.cs`).** `StopAsync` closes stdin, awaits `WaitForExitAsync` with a 500 ms token, and on timeout calls `_process.Kill(entireProcessTree: true)` in the catch — then returns immediately, **not** awaiting the kill to take effect. `RestartAsync` calls `StopAsync`, disposes/nulls `_process`, and calls `StartAsync` straight away. The new core opens the session directory and acquires its lock; if the killed core has not yet released the OS-level lock, the spawn races and can throw `SessionLockedException`.

- **`SessionViewModel` (`frontends/Dmon.Desktop/SessionViewModel.cs`).** `HandleToolConfirmAsync` and `HandleUiInputAsync` are `async void` (fire-and-forget subscriptions on `session.Events`). Each awaits an `Interaction<,>.Handle(...).FirstAsync()` then `_session.SendAsync(...)`. `ICoreSession.SendAsync` is documented as a no-op when the core is not Ready, but a mid-flight core death (client disposed/faulted underneath) can still throw; any throw in an `async void` continuation is posted to the UI `SynchronizationContext` as an unhandled exception and crashes the app.

## Goals / Non-Goals

**Goals:**
- A dead core (stdout EOF) or a pump fault promptly faults every outstanding `RequestAsync` with a clear, timeout-distinguishable exception, so callers fail fast instead of hanging to the 30 s timeout.
- A forced core kill during restart completes (process exit awaited) before the replacement core is spawned, so the session lock is released first.
- A failing host-directed interaction handler in Desktop surfaces as an error state, never as an unhandled exception on the UI thread.
- Targeted tests for each, all existing tests stay green.

**Non-Goals:**
- No change to the wire protocol, the typed-result correlation contract (ADR-015), or `IRpcTransport`/`IRpcClient`/`ICoreProcess` signatures.
- No new retry/reconnect logic — failures surface, they are not silently recovered.
- No change to the disposal-path fault type for pending requests (`DisposeAsync` keeps faulting with `ObjectDisposedException`).
- No new user-facing error UI beyond surfacing an error state (a logged diagnostic, or reuse of an existing surface) — a rich error dialog is out of scope.
- The other audit findings are untouched.

## Decisions

**D1 — Fault pending on non-cancellation pump exit; new `RpcTransportClosedException`.**
Add `core/Dmon.Runtime/RpcTransportClosedException.cs`: a `sealed class RpcTransportClosedException : Exception` mirroring `RpcTimeoutException` — carries `string CommandId`, an optional inner `Exception` cause, and a clear message ("the core exited or the transport closed before a result arrived for command '<id>'"). It is deliberately distinct from both `OperationCanceledException` and `RpcTimeoutException`.

In `RunPumpAsync`, distinguish three exits:
- **Cancellation** (the pump CTS fired — disposal): keep catching `OperationCanceledException` and do nothing to `_pending`; `DisposeAsync` owns faulting them (with `ObjectDisposedException`, unchanged).
- **Normal EOF** (the `await foreach` completes without throwing — the core's stdout reached end-of-stream): in the `finally`, fault every remaining `_pending` entry with `new RpcTransportClosedException(id, cause: null)`, then complete broadcast subscribers as today.
- **Non-OCE pump fault** (the transport read threw): **catch** it (do not let it escape `RunPumpAsync`), record it as the cause, and fault every remaining `_pending` entry with `new RpcTransportClosedException(id, cause: ex)`. Completing subscribers still happens in the `finally`.

Fault-then-remove each pending entry exactly once (`TryRemove` + `TrySetException`) so a request completing concurrently on a late correlated result is not double-completed. Because the pump and `DisposeAsync` both drain `_pending`, use `TryRemove`/`TrySetException` (both no-op on an already-removed/completed TCS) — the two paths are safe to interleave. On the disposal path the pump exits via OCE (leaving `_pending` intact) and `DisposeAsync` faults them; on the crash/EOF path the pump faults them and `DisposeAsync` finds the dictionary already drained. Net: a pending request is faulted once, by whichever path reached it first, and never hangs.

*Distinction rationale:* `RpcTimeoutException` means "the core is alive but slow / dropped this one command"; `RpcTransportClosedException` means "the channel is gone". Callers (e.g. the gateway create path) can map the latter to a distinct, non-retryable "core exited" condition rather than a `core_timeout`.

**D2 — Await process exit after `Kill` in `StopAsync`.**
In `CoreProcessManager.StopAsync`, after `_process.Kill(entireProcessTree: true)` in the timeout catch, `await _process.WaitForExitAsync(...)` with a **bounded** token (a fresh short budget, e.g. a few seconds) so the method waits for the OS to reap the process and release the session-directory lock before returning. Guard the wait so a second timeout or an already-exited process cannot throw out of `StopAsync` (best-effort, matching the existing `catch { }` around `Kill`). `RestartAsync` is unchanged in shape — it simply now observes that `StopAsync` has fully released the old process before `StartAsync` runs. The graceful (non-kill) path already awaits exit and is untouched.

**D3 — `try`/`catch` the async void handler bodies.**
Wrap the entire body of `HandleToolConfirmAsync` and `HandleUiInputAsync` in `try`/`catch (Exception ex)`. On catch, surface an error state rather than rethrowing: log the failure (a diagnostic sink / trace) and, where a surface exists, show it — the requirement is only that no exception escapes the `async void` continuation. Keep the happy path byte-for-byte identical. This is the minimal, correct fix for a fire-and-forget handler: an `async void` **must** own its exceptions because there is no returned task for anyone to await. The two handlers stay independent (one failing must not suppress the other).

**D4 — Spec deltas.** Two ADDED requirements on `host-rpc-transport` (pending-fault-on-close; restart-releases-lock) and one ADDED requirement on `desktop-host` (guarded interaction handlers). ADDED (not MODIFIED) because none of the existing requirements' guarantees change — these are new guarantees layered on. The existing "Request timeout" requirement is left intact; the new transport-closed fault is a *different* failure mode (channel gone vs. slow), so it is a separate requirement rather than an edit to the timeout block.

## Risks / Trade-offs

- **Do not mask a still-live core (D1).** The pump only faults pending on *actual* stream termination or a *real* pump fault; a live core that is merely slow keeps flowing events and its pending requests still resolve on their correlated results (or time out via the existing `RpcTimeoutException`). EOF on stdout is an unambiguous "core process ended" signal, so faulting on it cannot preempt a live core. The disposal path is explicitly excluded (OCE), so ordinary shutdown is unchanged.
- **Double-completion race (D1).** Mitigated by `TryRemove` + `TrySetException` — both the pump-exit drain and `DisposeAsync` are idempotent against an already-resolved/removed TCS, and a genuinely in-flight correlated result that lands first wins the `TryRemove` in the pump loop.
- **Bounded wait after Kill (D2).** Awaiting exit adds latency to a forced restart, capped by the bounded token; a wedged unkillable process cannot hang `StopAsync` indefinitely. If the bounded wait itself times out, `StopAsync` still returns (best-effort) — the restart then behaves as it does today, so this is strictly an improvement, not a new hang.
- **Error surfacing scope (D3).** The fix guarantees *no crash*; a full user-facing error dialog is deliberately out of scope. Logging (plus any existing surface) satisfies the requirement without expanding the Desktop UI.

## Open Questions

None blocking.
