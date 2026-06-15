## Why

Both the Terminal host and the Gateway hand-roll the same JSONL-over-stdio plumbing against the core process — serialize a `Command` as its polymorphic base, append a strict LF, flush; read lines, deserialize into `Event`, correlate typed `ResultEvent`s by `CommandId` (ADR-015). The logic is duplicated across `Dmon.Terminal/Program.cs`, `Dmon.Terminal/EventDispatcher.cs`, and `Dmon.Gateway/GatewayConnectionEndpoint.cs`, with subtle per-site framing rules (CR stripping, flush timing) that must stay byte-identical to the wire contract. There is no shared, testable seam for "talk to a core over the protocol", so the deferred composition-root-facets task 8.4 (a live provider → tool-call round-trip e2e test) has nowhere ergonomic to live, and the Terminal silently swallows the core's structured stderr, hiding launch and tool-call failures.

## What Changes

- Introduce `IRpcTransport` in `Dmon.Runtime`: a thin, framed send/receive seam over a core's stdio — `SendAsync(Command, …)` (canonical serialization + strict LF framing per ADR-003) and an `IAsyncEnumerable<Event>` event stream (canonical deserialization, blank-line/parse-error tolerant).
- Introduce `IRpcClient` in `Dmon.Runtime` over `IRpcTransport`: owns command-id correlation, typed `ResultEvent` matching (ADR-015), and timeouts — `SendAsync(Command, …)` for fire-and-forget and `RequestAsync<TResult>(Command, …) where TResult : ResultEvent` for request/response. A default implementation built from an `ICoreProcess`.
- Migrate the **Terminal host** fully onto `IRpcClient`/`IRpcTransport`, retiring `EventDispatcher` and the inline serialize/flush in `Program.cs`.
- Migrate the **Gateway's two-step create/load handshake** (`DriveSessionHandshakeAsync` + `ReadCorrelatedResultAsync`) onto `IRpcClient`. **Out of scope:** `SessionHandler`'s ongoing RPC loop (ADR-014 seq buffer, permission correlation) stays as-is.
- The Terminal host **forwards the core's stderr** (structured JSON log lines) instead of discarding it, surfacing core-side failures.
- Add an automated live e2e test driving a real core via `IRpcClient` through `agentReady` and a builtin tool-call round-trip, closing the deferred composition-root-facets **task 8.4**.

## Capabilities

### New Capabilities
- `host-rpc-transport`: a shared host-side abstraction (`IRpcTransport` + `IRpcClient`) for framed JSONL/stdio communication with a core process — command serialization, LF framing, event stream, command-id correlation, typed result matching, and timeouts.

### Modified Capabilities
- `console-host`: the host SHALL surface the core's stderr diagnostic stream rather than discarding it. (Consuming the shared `IRpcClient` in place of the inline plumbing is an implementation detail — the host's observable RPC behavior is unchanged — so it is not a spec-level requirement change.)

The Gateway create/load handshake is migrated onto the shared `IRpcClient` as well, but its observable behavior (typed correlated `created`, handshake results excluded from the seq stream, timeout) is preserved exactly, so `remote-session-gateway` requirements are unchanged — that migration is captured in `tasks.md`, not as a spec delta.

## Impact

- **New code:** `IRpcTransport`, `IRpcClient`, and a default `ICoreProcess`-backed implementation in `src/Dmon.Runtime`.
- **Modified code:** `src/Dmon.Terminal/Program.cs` (consume client; forward stderr), removal/retirement of `src/Dmon.Terminal/EventDispatcher.cs`; `src/Dmon.Gateway/GatewayConnectionEndpoint.cs` handshake path.
- **Tests:** new `Dmon.Runtime.Tests` for transport framing + client correlation/timeout; new live tool-call e2e test (closes 8.4); existing Gateway create e2e (`GatewayCreateE2ETests`) and Terminal tests updated to the new seam.
- **No wire-protocol change:** framing and the ADR-003/015 message shapes are unchanged — this is a host-side refactor plus a stderr-forwarding behavior change and added test coverage.
- **ADRs:** consistent with ADR-003 (framing), ADR-014 (seq buffer untouched), ADR-015 (typed result correlation). No new or superseding ADR required.
