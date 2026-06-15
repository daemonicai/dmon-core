## 1. Transport seam (`IRpcTransport`)

- [x] 1.1 Add `IRpcTransport` to `src/Dmon.Runtime`: `Task SendAsync(Command, CancellationToken)` and an inbound `IAsyncEnumerable<Event>` event stream; XML-doc the framing contract (canonical `WireSerializerOptions.Default`, base-type polymorphic serialization, single trailing `\n`, no `\r`).
- [x] 1.2 Add a default implementation constructible from an `ICoreProcess` (reads `StandardOutput`, writes `StandardInput`); send path writes `json + "\n"` and flushes; receive path deserializes lines into the polymorphic `Event` base type.
- [x] 1.3 Make the event stream resilient: skip blank/whitespace lines; on a malformed line emit a parse diagnostic and continue (do not fault the stream); complete normally at end-of-stdout.
- [x] 1.4 Unit tests in `test/Dmon.Runtime.Tests`: exact-bytes framing (single `\n`, no `\r`, correct discriminator) via a capturing writer; blank-line skip; malformed-line tolerance; clean completion at end-of-stream. Reuse/extend the existing `FeedableReader`/`CapturingWriter` doubles.

## 2. Correlated client (`IRpcClient`)

- [ ] 2.1 Add `IRpcClient` over `IRpcTransport`: `Task SendAsync(Command, CancellationToken)` (fire-and-forget) and `Task<TResult> RequestAsync<TResult>(Command, CancellationToken) where TResult : ResultEvent`.
- [ ] 2.2 Implement single-reader fan-out: one read loop drains the transport's event stream, feeds correlation (pending `commandId → TaskCompletionSource<ResultEvent>`) AND a broadcast subscription, so non-matching events remain observable to other consumers.
- [ ] 2.3 Implement request/response correlation by `CommandId` (ADR-015) with a finite timeout that faults with a dedicated timeout-distinguishable exception (e.g. `RpcTimeoutException`), distinct from `OperationCanceledException`.
- [ ] 2.4 Unit tests in `test/Dmon.Runtime.Tests`: request completes on the correlated typed result; unrelated `ResultEvent` does not complete the request and is still delivered to the broadcast consumer; timeout faults distinctly from cancellation.

## 3. Terminal host migration

- [ ] 3.1 Migrate `src/Dmon.Terminal/Program.cs` to issue commands and consume events via `IRpcClient`/`IRpcTransport` instead of inline serialize/flush.
- [ ] 3.2 Retire `src/Dmon.Terminal/EventDispatcher.cs` (its channel/dispatch role is subsumed by the client's event stream), updating call sites and DI.
- [ ] 3.3 Update `test/Dmon.Terminal.Tests` to the new seam; confirm event dispatch, reload, and input-handling tests still pass.

## 4. Terminal stderr forwarding

- [ ] 4.1 Route the core's stderr to a host diagnostic sink (host stderr and/or a scrollback diagnostics line) via `CoreProcessManager`'s existing `onStderrLine` callback — the Terminal currently passes `null`. Must not interleave into conversational scrollback and must not block the RPC event loop.
- [ ] 4.2 Test the `console-host` scenarios: a core stderr line is forwarded to the diagnostic sink (not discarded); a startup-failure line written to stderr is surfaced.

## 5. Gateway handshake migration

- [ ] 5.1 Migrate `DriveSessionHandshakeAsync` + `ReadCorrelatedResultAsync<T>` in `src/Dmon.Gateway/GatewayConnectionEndpoint.cs` to drive `session.create` → path-less `session.load` through `IRpcClient.RequestAsync<TResult>`; map `RpcTimeoutException` → `core_timeout`. Leave `SessionHandler`'s ongoing RPC loop untouched.
- [ ] 5.2 Confirm `GatewayCreateE2ETests` stays green against the migrated path — especially 2.3 (handshake results excluded from the seq stream, ADR-014) and 2.4 (timeout/exception/cap), using the existing `FakeCoreProcess`/`FeedableReader` doubles.

## 6. Live tool-call e2e test (closes composition-root-facets 8.4)

- [ ] 6.1 Add an automated e2e test that launches a real core via `ICoreLauncher`, wraps it in `IRpcClient`, awaits `agentReady`, submits a turn that triggers a builtin tool, and asserts the tool-call round-trip. Bound by the `RequestAsync` timeout; forwarded stderr (Group 4) surfaces failures.
- [ ] 6.2 On missing provider config (ADR-005), the test SHALL skip with a logged reason — never a silent pass. Retain existing partial coverage (`BuiltinToolsRegistrationTests`, `CoreProcessFixture`).
- [ ] 6.3 Tick `composition-root-facets` task 8.4 in `openspec/changes/composition-root-facets/tasks.md` (flip `[ ]→[x]` only) noting it is now covered by this change's e2e test.

## 7. Gates

- [ ] 7.1 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 7.2 `make test` green — new `Dmon.Runtime.Tests`, updated Terminal/Gateway tests, and the live e2e (or its honest skip).
- [ ] 7.3 `openspec validate host-rpc-abstraction --strict`.
