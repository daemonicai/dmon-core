## Why

`Dmon.Core`'s command dispatch is an imperative loop that parses each stdin line, reads the `"type"` string by hand, routes through a large string `switch`, and only then deserializes to a concrete command. This carries three avoidable liabilities: (1) the routing `switch` duplicates the `[JsonDerivedType]` discriminator map already declared on `Command`, so adding a command means editing two places that can silently drift; (2) long-running commands need a `JsonElement`/`JsonDocument`-lifetime "deserialize eagerly before dispose" dance because routing happens before deserialization; and (3) error handling is split across two near-identical `catch` sites. Reworking the read path into a small pipeline of *total* stages removes all three while keeping the wire protocol and every observable behavior intact.

## What Changes

- Replace the `while`/`ReadLineAsync` loop in `RpcHostedService` with an `IAsyncEnumerable<string>` line **source stage** (`ReadLinesAsync`) that owns CRLF-trimming, blank-skipping, and stdin-EOF/cancellation semantics. Strictly single-reader and sequential — no concurrency introduced.
- Introduce a **total parse stage** (`ParseCommand`) that converts a line into a closed result — `ParsedCommand(Command)` or `ParseFault(ErrorEvent)` — and **never throws**. It deserializes against the base `Command` type (polymorphic on `"type"`), so the `[JsonDerivedType]` table becomes the single routing source of truth and the hand-written string `switch` is removed.
- Route in a **typed sink** on the concrete CLR command type (pattern `switch`), inside a single error guard (`RunGuardedAsync`) shared by both the inline and backgrounded paths — collapsing the two duplicated `catch` blocks into one.
- Because commands are materialized to POCOs *before* routing, the long-running offload (`turn.submit`, `wizard.start`) simply captures the POCO; the eager-deserialize/`JsonDocument`-lifetime apparatus is deleted.
- **Preserve all five error codes** (`malformedCommand`, `missingType`, `unknownCommand`, `notImplemented`, `internalError`) and their `recoverable` flags via a parse → peek-`type` → deserialize sequence. One accepted, documented nuance: a malformed *payload of a known command type* now yields `unknownCommand` (recoverable) instead of `internalError` (non-recoverable) — strictly more correct (see design.md).
- **No new dependencies.** `IAsyncEnumerable<T>` and the existing `System.Threading.Channels` only; explicitly **not** `System.Reactive`.
- **Not** changing: the JSONL/stdio wire format (ADR-003), the `agentReady` handshake, the `turn.submit`↔`tool.confirmResponse` TCS suspension model, or the Terminal host (already `Channel<Event>`).

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `agent-core`: ADD a requirement codifying malformed/missing/unknown command handling (the reader emits an `error` event and continues; it never terminates the dispatch loop on bad input). This behavior exists today but is unspecified; the refactor is the moment to pin it. The existing "Dispatch loop does not block on long-running commands" requirement is held invariant (its scenarios are the refactor's regression net) — no wording change.

## Impact

- **Code:** `src/Dmon.Core/Rpc/RpcHostedService.cs` (loop → source stage), `src/Dmon.Core/Rpc/CommandDispatcher.cs` (parse-then-string-switch → total `ParseCommand` + typed sink; remove `BuildBackgroundWork`/`DeserializeAndBind` lifetime dance and the duplicate string `switch`). Possible small new internal types (`CommandParse`/`ParsedCommand`/`ParseFault`).
- **Wire/ABI:** none — JSONL shape, command/event discriminators, and protocol version are unchanged.
- **Tests:** `test/Dmon.Core.Tests/Rpc/DispatchLoopIntegrationTests.cs` (4 real-loop integration tests) must stay green unchanged; add unit tests for the now-pure `ParseCommand` covering each error code, unknown discriminator, and the documented payload-error nuance.
- **Dependencies:** none added.
- **Specs/ADRs:** `agent-core` spec gains one requirement; ADR-003 untouched (wire-only ADR, internal mechanism not constrained by it).
