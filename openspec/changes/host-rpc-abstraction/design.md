## Context

Two host surfaces drive a `Dmon.Core` process over JSONL/stdio (ADR-003): the Terminal host and the Gateway. Both hand-roll the same framing and correlation:

- **Terminal** — `src/Dmon.Terminal/Program.cs` serializes a `Command` as its base type with `WireSerializerOptions`, writes `json + "\n"`, flushes; `src/Dmon.Terminal/EventDispatcher.cs` reads stdout lines into an `Event` channel.
- **Gateway** — `src/Dmon.Gateway/GatewayConnectionEndpoint.cs` `DriveSessionHandshakeAsync` writes the same framed commands and `ReadCorrelatedResultAsync<T>` reads lines, deserializes `Event`, and filters for a typed `ResultEvent` subclass whose `CommandId` matches (ADR-015).

`Dmon.Runtime` already owns the process seam: `ICoreLauncher` → `CoreSession(ICoreProcess, AgentReadyEvent)`, where `ICoreProcess` exposes `StandardOutput`/`StandardInput` and `CoreProcessManager` drains stderr via an optional `onStderrLine` callback. The Terminal passes `null`, so core stderr is read-and-discarded — hiding failures. There is no shared "speak the protocol to a core" abstraction above `ICoreProcess`, which is why composition-root-facets task 8.4 (a live provider → tool-call round-trip e2e test) was deferred here.

Decisions confirmed with the user: migrate the **Terminal fully** and the **Gateway create/load handshake only** (leave `SessionHandler`'s ongoing loop — ADR-014 seq buffer, permission correlation — untouched); `IRpcClient` owns **request/response correlation + timeout**, `IRpcTransport` stays a thin framed send/receive over `IAsyncEnumerable<Event>`.

## Goals / Non-Goals

**Goals:**
- A single, unit-testable framing seam (`IRpcTransport`) and a single correlation/timeout seam (`IRpcClient`) in `Dmon.Runtime`, both constructible from an `ICoreProcess`.
- Terminal and the Gateway handshake consume the shared seam; the byte-level wire contract (ADR-003 framing, ADR-015 typed correlated results) is preserved exactly.
- Terminal forwards core stderr.
- An automated live e2e test driving a real core via `IRpcClient` through `agentReady` + a builtin tool-call round-trip — closing deferred task 8.4.

**Non-Goals:**
- No change to the wire protocol, message shapes, or `WireSerializerOptions`.
- No migration of `SessionHandler`'s ongoing RPC loop, its ADR-014 `seq` buffer, or permission correlation.
- No change to `ICoreLauncher`/`ICoreProcess`/`CoreResolver` resolution or lifecycle semantics.
- No new ADR.

## Decisions

### D1. Two seams, not one
`IRpcTransport` owns framing only: `Task SendAsync(Command, CancellationToken)` and `IAsyncEnumerable<Event> Events { get; }` (or `ReadEventsAsync(CancellationToken)`). `IRpcClient` composes a transport and adds `Task SendAsync(Command, …)` (fire-and-forget) plus `Task<TResult> RequestAsync<TResult>(Command, …) where TResult : ResultEvent`. *Why:* the gateway needs request/response (`created`), the Terminal mostly needs fire-and-forget + a broadcast event stream; separating framing from correlation keeps each testable in isolation and lets the Terminal subscribe to the raw event stream while still issuing correlated requests. *Alternative rejected:* one fat client conflating framing and correlation — harder to test framing edge cases (CR stripping, malformed lines) without a correlation harness.

### D2. Single reader, fan-out to one event stream
The transport owns the **single** consumer of `ICoreProcess.StandardOutput`; `IRpcClient` multiplexes that one stream so that `RequestAsync` correlation and the host's broadcast subscription both observe every event without competing reads. *Why:* two readers on one `TextReader` race and drop lines. Correlation peeks for the matching `CommandId` while non-matching events continue to flow to the broadcast consumer (spec: "non-matching results remain observable"). Likely shape: a single read loop pumping a `Channel<Event>` (or a small broadcast), with a registry of pending `(commandId → TaskCompletionSource<ResultEvent>)`.

### D3. Timeout is transport-distinguishable
`RequestAsync` takes a finite timeout and faults with a dedicated timeout exception type (e.g. `RpcTimeoutException`), distinct from `OperationCanceledException`, so the gateway maps timeout → `core_timeout` while caller cancellation tears down normally. *Why:* the existing create path already distinguishes these outcomes; collapsing them to `OperationCanceledException` would lose the `core_timeout` signal.

### D4. Terminal stderr forwarding via the existing `onStderrLine` seam
`CoreProcessManager` already supports an `onStderrLine` callback; the Terminal currently passes `null`. The fix routes core stderr to a host diagnostic sink (host stderr and/or a scrollback diagnostics line), off the RPC event path. *Why:* reuses the existing drain seam — no new process plumbing — and satisfies the `console-host` ADDED requirement. Forwarding must not interleave into conversational scrollback nor block the event loop.

### D5. Live e2e test closes 8.4 in `Dmon.Runtime.Tests` (or a dedicated e2e project)
The 8.4 acceptance becomes an automated test: launch a real core via `ICoreLauncher`, wrap it in `IRpcClient`, await `agentReady`, submit a turn that triggers a builtin tool, and assert the tool-call round-trip. *Why:* `IRpcClient` makes driving a real core ergonomic, and forwarded stderr (D4) makes failures diagnosable. The test is gated on a configured provider per ADR-005; where no live provider is available in CI it must degrade to a clear skip rather than a silent pass (see Risks). Existing partial coverage (`BuiltinToolsRegistrationTests`, `CoreProcessFixture` reaching `agentReady`) is retained.

### D6. Migration order minimizes risk
Land transport + client + unit tests first (no call-site change), then migrate the Terminal (retire `EventDispatcher`), then the Gateway handshake (`GatewayCreateE2ETests` must stay green against the new seam, using its `FakeCoreProcess`/`FeedableReader` doubles), then the live e2e + stderr forwarding.

### D7. Gateway handshake uses a non-read-ahead transport-level correlated request, not the continuous-pump `IRpcClient`
Driving the create/load handshake through `IRpcClient.RequestAsync` is **unsafe** for the gateway: `RpcClient`'s single read-pump consumes the transport's event stream continuously and republishes into a bounded, lossy (`DropOldest`) broadcast. After the `session.loadResult` correlates, the pump can read the *next* line (a post-handshake event the core emits before `SessionHandler` attaches) in the unsynchronizable window before disposal — pulling it out of the `TextReader` `SessionHandler` must read and risking a silent drop. That corrupts the ADR-014 `seq` stream (fenced by `GatewayCreateE2ETests` 2.3) and violates this change's own non-goal ("do not feed the seq stream from the lossy broadcast"). The continuous pump is correct for the Terminal (broadcast subscriber) but wrong for the handshake, which needs *read-exactly-until-match-then-stop* semantics so the reader is handed to `SessionHandler` in a known position.

*Decision:* add a small lossless correlated-request helper in `Dmon.Runtime` built on `IRpcTransport` (send the command, then `await foreach` over `transport.Events` and **return/break on the first `ResultEvent` whose `CommandId` matches** — the suspended async iterator never reads the next line, so there is **zero read-ahead**). The handshake migrates onto this helper, reusing the canonical `WireSerializerOptions.Default` framing + deserialization seam (the actual duplication the proposal targets) while preserving the line-precise stop the current code relies on. Timeout is a finite bound that faults `RpcTimeoutException` (→ `core_timeout`, design D3); caller cancellation stays `OperationCanceledException`. The helper returns the base `ResultEvent` so the gateway pattern-matches `SessionCreatedResultEvent` / `SessionLoadedResultEvent` / `CommandErrorEvent` / unexpected exactly as today (no `InvalidCastException` swallowing of a command error). `SessionHandler`'s ongoing loop is untouched and continues to own the lossless `seq` read of the same `StreamReader`.

*Why not `RequestAsync`:* see above — it cannot be made read-ahead-safe without either a "stop-after-match" mode on the pump or feeding `SessionHandler` from the lossy broadcast, both of which are out of scope and the latter explicitly forbidden by ADR-014.

## Risks / Trade-offs

- **Byte-level framing regression** (a stray `\r`, missed flush, double-encoded discriminator) silently breaks the wire → Mitigation: transport unit tests assert exact bytes (single `\n`, no `\r`, base-type polymorphic discriminator) against a `CapturingWriter`; keep `WireSerializerOptions.Default` as the sole serializer.
- **Single-reader fan-out drops or reorders events** under the Terminal's concurrent subscribe + request → Mitigation: one read loop only; correlation and broadcast both fed from it; reuse/extend the existing `FeedableReader` test doubles to assert ordering and "non-matching event still delivered".
- **Live e2e flakiness / no provider in CI** → Mitigation: bound with the `RequestAsync` timeout; on missing provider config, **skip with a logged reason** (never a silent pass) so 8.4 coverage status is honest.
- **Gateway handshake behavior drift** (handshake results must stay out of the ADR-014 seq stream) → Mitigation: `GatewayCreateE2ETests` 2.3 (seq-ordering) and 2.4 (timeout/exception/cap) are the regression fence; run them against the migrated path before ticking.
- **Scope creep into `SessionHandler`** → Mitigation: explicit non-goal; the ongoing loop keeps its own read of stdout for now (documented), accepting that full deduplication is a later change.

## Migration Plan

1. Add `IRpcTransport`, `IRpcClient`, default impls + unit tests in `Dmon.Runtime`. No call sites change.
2. Migrate Terminal onto `IRpcClient`; delete `EventDispatcher`; update `Dmon.Terminal.Tests`.
3. Add Terminal stderr forwarding (D4) + `console-host` scenario coverage.
4. Migrate the Gateway handshake (`DriveSessionHandshakeAsync`/`ReadCorrelatedResultAsync`) onto `IRpcClient`; keep `GatewayCreateE2ETests` green.
5. Add the live tool-call e2e test (closes 8.4); tick composition-root-facets 8.4 in its `tasks.md`.

Rollback: the change is host-side only with no wire change; reverting the commits restores the inline plumbing without any persisted-state or protocol migration.

## Open Questions

- Should the live e2e test live in `Dmon.Runtime.Tests` or a new `Dmon.E2E.Tests` project? (Leaning: reuse an existing test project to avoid solution churn; decide during apply.)
- Exact stderr sink for the Terminal — dedicated scrollback diagnostics region vs. host process stderr. (Leaning: host stderr by default so it composes with redirection; spec leaves the sink open.)
