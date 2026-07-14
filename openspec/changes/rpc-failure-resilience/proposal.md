## Why

Three host-side failure paths let a dead or crashing core degrade into a hang or a process crash instead of a clean, fast failure. All three are verified present on `main`.

- **#12 — `RpcClient` pump exit does not fault pending requests.** `core/Dmon.Runtime/RpcClient.cs`'s `RunPumpAsync` `finally` (lines 272–280) only completes broadcast subscribers; it does not fault `_pending`. Outstanding `RequestAsync<TResult>` `TaskCompletionSource`s are faulted only in `DisposeAsync` (lines 226–229). The pump catches only `OperationCanceledException` (line 268), so when the child's stdout reaches EOF (core exited) — or the pump throws a non-OCE exception — in-flight `RequestAsync` calls do not observe the closure and hang until their 30s request timeout elapses. A non-OCE pump fault is additionally left unobserved by `DisposeAsync`'s `await _pumpTask` (which catches only OCE).

- **#13 — `CoreProcessManager.StopAsync` kill path does not wait for exit.** `core/Dmon.Runtime/CoreProcessManager.cs`'s graceful path awaits `WaitForExitAsync` with a 500 ms budget (line 108), but on timeout the catch calls `_process.Kill(entireProcessTree: true)` (line 112) and returns **without awaiting exit**. `RestartAsync` (lines 121–127) then disposes/nulls the process and immediately calls `StartAsync`, relying on the fire-and-forget kill having already released the OS-level session lock. When the kill has not completed, the fresh spawn can throw `SessionLockedException` because the old process still holds the session directory lock.

- **#8 — Unguarded `async void` interaction handlers in Desktop.** `frontends/Dmon.Desktop/SessionViewModel.cs`'s `HandleToolConfirmAsync` (line 147) and `HandleUiInputAsync` (line 182) are `async void` with no `try`/`catch`. The `await ...Handle(...).FirstAsync()` and `await _session.SendAsync(...)` calls (lines 157/179 and 190/199) can throw — most plausibly when a dead core makes `SendAsync` fail — into a void continuation, which becomes an unhandled exception on the UI thread and crashes the process.

Each is a "survive a dead/crashing core gracefully" gap: the correct outcome is fail fast (fault pending, release the lock, surface an error state), not hang or crash.

## What Changes

- **#12 — Fault pending requests on pump exit.** On any pump exit that is **not** caller-initiated cancellation — normal stdout EOF (core exited) or a non-OCE pump exception — `RpcClient` faults every outstanding `_pending` `TaskCompletionSource` with a new, timeout-distinguishable `RpcTransportClosedException` (carrying the pending command id and, for a fault, the underlying cause) so callers fail fast with a clear "core exited / transport closed" error instead of waiting out the request timeout. A non-OCE pump exception is observed (not left to fault `DisposeAsync`'s `await _pumpTask`). Caller-initiated cancellation (disposal) keeps its existing behaviour: pending are faulted by `DisposeAsync`.
- **#13 — Await process exit after Kill.** `CoreProcessManager.StopAsync` awaits `WaitForExitAsync` (bounded) **after** `Kill(entireProcessTree: true)`, so the OS has released the session-directory lock before `StopAsync` returns and `RestartAsync` spawns the replacement — closing the `SessionLockedException` race on restart.
- **#8 — Guard the async void handlers.** `HandleToolConfirmAsync` and `HandleUiInputAsync` wrap their bodies in `try`/`catch`, so a dead-core `SendAsync` failure or an unhandled interaction surfaces as a logged/shown error state rather than an unhandled exception on the UI thread.

No wire-protocol shape change, no `IRpcTransport`/`IRpcClient`/`ICoreProcess` signature change, no host UX change beyond faster, cleaner failures. Honours ADR-015 (typed command results are untouched — these are resilience additions on the transport/lifecycle layer).

## Capabilities

### New Capabilities

_None — this change adds resilience guarantees to two existing capabilities; it introduces no new capability._

### Modified Capabilities

- `host-rpc-transport`: **MODIFIED** (two ADDED requirements) — (a) outstanding correlated requests fault promptly with a timeout-distinguishable transport-closed exception when the core exits or the pump faults, instead of hanging to the request timeout; (b) a core-process restart awaits the old process's exit after a forced kill so the session lock is released before the replacement spawns.
- `desktop-host`: **MODIFIED** (one ADDED requirement) — the host-directed interaction handlers (`tool.confirmRequest`, UI input) never let a failure escape as an unhandled exception on the UI thread; a failed round-trip (e.g. a dead-core send) surfaces as an error state.

## Impact

- **Code:**
  - `core/Dmon.Runtime/RpcClient.cs` (fault `_pending` on non-cancellation pump exit; observe non-OCE pump faults).
  - `core/Dmon.Runtime/RpcTransportClosedException.cs` (new exception type, mirroring `RpcTimeoutException`).
  - `core/Dmon.Runtime/CoreProcessManager.cs` (await `WaitForExitAsync` after `Kill` in `StopAsync`).
  - `frontends/Dmon.Desktop/SessionViewModel.cs` (`try`/`catch` around both `async void` handler bodies).
- **Tests:** `test/Dmon.Runtime.Tests/RpcClientTests.cs` (pump-exit faults pending — the `FeedableTransport.Complete()` seam already exists), a `CoreProcessManager` restart/stop test in `test/Dmon.Runtime.Tests`, and a `SessionViewModel` handler-failure test in `test/Dmon.Desktop.Tests`.
- **ADRs:** conforms to ADR-015 as written — **no amending/superseding ADR**.
- **No impact:** wire protocol shape, the typed-result correlation contract (ADR-015), session storage, providers, the agent core, the gateway wire contract. Out of scope: every other audit finding.
