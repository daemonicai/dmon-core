## 1. Fault pending requests on pump exit (#12)

- [x] 1.1 Investigate `core/Dmon.Runtime/RpcClient.cs`: confirm `RunPumpAsync` (~lines 250–281) catches only `OperationCanceledException` and that its `finally` completes broadcast subscribers but never faults `_pending`; confirm `_pending` is faulted only in `DisposeAsync` (~lines 226–229) with `ObjectDisposedException`; confirm `DisposeAsync`'s `await _pumpTask` catches only `OperationCanceledException` (so a non-OCE pump fault would resurface there). Cross-check `core/Dmon.Runtime/RpcTimeoutException.cs` as the exception template and `FeedableTransport.Complete()` in `test/Dmon.Runtime.Tests/RpcClientTests.cs` as the EOF test seam.
- [x] 1.2 Add `core/Dmon.Runtime/RpcTransportClosedException.cs`: a `sealed class RpcTransportClosedException : Exception` mirroring `RpcTimeoutException` — a `string CommandId` property, an optional inner cause, and a clear message ("the core exited or the transport closed before a result arrived for command '<id>'"). Distinct from both `OperationCanceledException` and `RpcTimeoutException` (D1).
- [x] 1.3 In `RunPumpAsync`, on a non-cancellation exit — normal `await foreach` completion (stdout EOF) **or** a caught non-OCE pump exception — fault every remaining `_pending` entry via `TryRemove` + `TrySetException(new RpcTransportClosedException(id, cause))` (cause = null on EOF, the caught exception on fault), once each, then complete broadcast subscribers as today. Catch the non-OCE exception inside `RunPumpAsync` so it does not escape and resurface at `DisposeAsync`. Leave the cancellation (disposal) path untouched: it still catches `OperationCanceledException`, leaves `_pending` for `DisposeAsync`, which continues to fault with `ObjectDisposedException` (D1).

## 2. Await process exit after Kill (#13)

- [x] 2.1 Investigate `core/Dmon.Runtime/CoreProcessManager.cs`: confirm `StopAsync` (~lines 91–115) kills the tree in the `WaitForExitAsync`-timeout catch (line 112) and returns without awaiting exit, and that `RestartAsync` (~lines 121–127) stops → disposes/nulls → `StartAsync` immediately.
- [x] 2.2 In `StopAsync`, after `_process.Kill(entireProcessTree: true)`, `await _process.WaitForExitAsync(...)` with a fresh **bounded** token (short budget) so the OS releases the session-directory lock before `StopAsync` returns. Guard the wait (best-effort, like the existing `catch { }` around `Kill`) so a second timeout or an already-exited process cannot throw out of `StopAsync`. Leave the graceful (non-kill) path — which already awaits exit — unchanged (D2).

## 3. Guard the Desktop async void handlers (#8)

- [ ] 3.1 Investigate `frontends/Dmon.Desktop/SessionViewModel.cs`: confirm `HandleToolConfirmAsync` (~line 147) and `HandleUiInputAsync` (~line 182) are `async void` with no `try`/`catch`, awaiting `Interaction.Handle(...).FirstAsync()` (157/190) and `_session.SendAsync(...)` (179/199). Identify an available diagnostic/log surface for the error state.
- [ ] 3.2 Wrap the entire body of each handler in `try`/`catch (Exception ex)`; on catch, surface an error state (log; show where a surface exists) rather than rethrowing — no exception may escape the `async void` continuation. Keep the happy path byte-for-byte identical and keep the two handlers independent (D3).

## 4. Tests

- [x] 4.1 In `test/Dmon.Runtime.Tests/RpcClientTests.cs`, add a test that starts an `RpcClient` over `FeedableTransport`, issues a `RequestAsync<TResult>`, calls `transport.Complete()` (stdout EOF), and asserts the awaiting request faults promptly with `RpcTransportClosedException` (not `RpcTimeoutException`, not a hang to the request timeout). Add a companion test that a non-OCE pump fault faults pending with `RpcTransportClosedException` carrying the cause and does not resurface at `DisposeAsync`.
- [x] 4.2 In `test/Dmon.Runtime.Tests`, add a `CoreProcessManager` test that exercises stop/restart when the graceful shutdown times out (forcing the kill path) and asserts the replacement spawn does not hit a `SessionLockedException` (i.e. the old process's lock is released before respawn). Use a bounded/real short-lived core-launch harness consistent with existing runtime tests.
- [ ] 4.3 In `test/Dmon.Desktop.Tests`, add a `SessionViewModel` test using the existing `FakeCoreSession` seam configured to throw from `SendAsync`, dispatch a `ToolConfirmRequestEvent` (and a `UiInputRequestEvent`), and assert no exception escapes to the UI thread (the handler catches and surfaces an error state).

## 5. Gates and spec alignment

- [ ] 5.1 `make build` clean (TreatWarningsAsErrors on).
- [ ] 5.2 `env -u MEKO_API_KEY make test` green — the new tests plus all existing tests (pkill a stale `Everything.slnx` testhost first if it hangs).
- [ ] 5.3 `openspec validate rpc-failure-resilience --strict` passes; both deltas (`host-rpc-transport` two ADDED requirements, `desktop-host` one ADDED requirement) match the implemented behaviour.
