## Purpose

The host RPC transport is the wire-framing and correlation layer that hosts (the console host and the gateway) use to talk to an agent core process over JSONL/stdio (ADR-003). It lives in `Dmon.Runtime` and is split into two seams: an `IRpcTransport` that owns line framing and the inbound event stream over a single core's stdio, and an `IRpcClient` over it that owns command-id correlation, typed result matching (ADR-015), and request timeouts. This replaces inline per-host stdio plumbing with a shared, testable abstraction.
## Requirements
### Requirement: Framed JSONL transport over a core's stdio

`Dmon.Runtime` SHALL expose an `IRpcTransport` abstraction that owns the wire framing for a single core process and nothing above it. `SendAsync(Command, CancellationToken)` SHALL serialize the command as its polymorphic base `Command` type using the canonical `WireSerializerOptions.Default`, write the JSON followed by a single `\n`, and flush — with no `\r` ever emitted (strict LF framing, ADR-003). `SendAsync` SHALL write each frame atomically and SHALL serialize concurrent callers, so that two or more overlapping `SendAsync` calls on the same transport can never interleave their bytes or terminators; each emitted line SHALL be one whole frame terminated by exactly one `\n`. The transport SHALL expose the inbound stream as `IAsyncEnumerable<Event>`, deserializing each line with the same canonical options into the polymorphic `Event` base type. A default implementation SHALL be constructible from an `ICoreProcess` (reading its `StandardOutput`, writing its `StandardInput`).

#### Scenario: Command is framed with a single trailing LF
- **WHEN** a caller invokes `SendAsync` with a `Command`
- **THEN** the transport writes the command's canonical JSON followed by exactly one `\n`, contains no `\r`, and flushes the stream

#### Scenario: Concurrent sends do not interleave
- **WHEN** multiple callers invoke `SendAsync` concurrently on the same transport (for example a host whose event handlers dispatch sends without awaiting each other)
- **THEN** every frame is written whole and terminated by exactly one `\n`, no frame's bytes are interleaved with another's, and each `\n`-delimited line deserializes back into the exact `Command` that was sent

#### Scenario: Inbound lines surface as typed events
- **WHEN** the core writes a JSONL event line to stdout
- **THEN** the transport's `IAsyncEnumerable<Event>` yields the corresponding polymorphic `Event` subtype deserialized with `WireSerializerOptions.Default`

### Requirement: Resilient inbound event stream

The transport's event stream SHALL be tolerant of non-event noise on the channel: blank or whitespace-only lines SHALL be skipped, and a line that fails to deserialize SHALL NOT terminate the stream — it SHALL be reported as a diagnostic and consumption SHALL continue with the next line. The stream SHALL complete normally when the core's stdout reaches end-of-stream.

#### Scenario: Blank lines are skipped
- **WHEN** the core emits a blank line between two event lines
- **THEN** the stream yields the two events and does not yield or fault on the blank line

#### Scenario: Malformed line does not kill the stream
- **WHEN** a line on stdout is not valid event JSON
- **THEN** the transport reports a parse diagnostic and continues yielding subsequent valid events

#### Scenario: Stream completes at end-of-stdout
- **WHEN** the core's stdout reaches end-of-stream
- **THEN** the event enumerable completes normally without throwing

### Requirement: Correlated request/response client

`Dmon.Runtime` SHALL expose an `IRpcClient` over `IRpcTransport` that owns command-id correlation, typed result matching (ADR-015), and timeouts. `SendAsync(Command, CancellationToken)` SHALL dispatch a command without awaiting a result. `RequestAsync<TResult>(Command, CancellationToken) where TResult : ResultEvent` SHALL dispatch the command and complete with the first inbound `TResult` whose `CommandId` equals the dispatched command's `id`. Events that are not the awaited correlated result SHALL remain observable to other consumers of the client (the request path SHALL NOT swallow unrelated events).

#### Scenario: Request completes on the correlated typed result
- **WHEN** a caller invokes `RequestAsync<SessionCreatedResultEvent>` for a `session.create` command with id `c1` and the core later emits a `SessionCreatedResultEvent` with `CommandId` `c1`
- **THEN** the call completes with that event

#### Scenario: Non-matching results are ignored by the request
- **WHEN** a `RequestAsync<TResult>` is awaiting command id `c1` and the core emits a `ResultEvent` correlated to a different command id
- **THEN** the awaiting request does not complete on that event and the unrelated event remains observable to other consumers

### Requirement: Request timeout

`RequestAsync<TResult>` SHALL accept a bounded wait and SHALL fault with a timeout-distinguishable exception (not a generic cancellation) when no correlated result arrives within the bound, so callers such as the gateway create path can map it to an actionable `core_timeout`.

#### Scenario: Timeout when no correlated result arrives
- **WHEN** a caller issues a `RequestAsync<TResult>` with a finite timeout and the core never emits a result correlated to that command
- **THEN** the call faults with a timeout-distinguishable exception once the timeout elapses, distinct from caller-initiated cancellation

### Requirement: Pending requests fault when the core exits or the pump faults

`IRpcClient` SHALL fault every outstanding `RequestAsync<TResult>` promptly when its inbound event pump exits for any reason other than caller-initiated cancellation — that is, when the core's stdout reaches end-of-stream (the core process exited) or the pump observes a non-cancellation exception. On such an exit the client SHALL complete each pending correlated request with a timeout-distinguishable transport-closed exception (distinct from both `OperationCanceledException` and the request-timeout exception), so callers fail fast with a clear "core exited / transport closed" error instead of waiting out the per-request timeout. A non-cancellation pump exception SHALL be observed by the client (it SHALL NOT escape unobserved to surface later at disposal). Caller-initiated cancellation (disposal) SHALL retain its existing behaviour of faulting pending requests at disposal. Each pending request SHALL be faulted at most once regardless of which path (pump exit or disposal) reaches it first.

#### Scenario: Core exit faults in-flight requests immediately
- **WHEN** a caller is awaiting a `RequestAsync<TResult>` and the core's stdout reaches end-of-stream before any correlated result arrives
- **THEN** the awaiting request faults promptly with the transport-closed exception (distinct from the request-timeout exception), rather than blocking until the per-request timeout elapses

#### Scenario: Pump fault does not resurface at disposal
- **WHEN** the inbound pump throws a non-cancellation exception while a request is outstanding
- **THEN** the outstanding request faults with the transport-closed exception carrying the underlying cause, and the pump exception is observed by the client so it does not resurface when the client is later disposed

#### Scenario: Cancellation-initiated shutdown is unchanged
- **WHEN** the client is disposed (its pump stops via caller-initiated cancellation) while a request is outstanding
- **THEN** the outstanding request is faulted by the disposal path as before, and is faulted exactly once

### Requirement: Core process restart releases the session lock before respawn

The core-process manager SHALL, when forcibly terminating a core process during stop, await the process's exit after issuing the kill so that the operating system has released the core's session-directory lock before the stop operation returns. A restart SHALL therefore not spawn the replacement core until the previous process has fully exited, closing the race in which the fresh spawn fails to acquire the session lock still held by the process being killed. The await of exit SHALL be bounded so a wedged, unkillable process cannot hang the stop operation indefinitely; if the bounded wait elapses the stop SHALL still return (best-effort), never worse than today.

#### Scenario: Killed core is awaited before the replacement spawns
- **WHEN** a stop times out its graceful shutdown and forcibly kills the core process, and a restart then spawns a replacement
- **THEN** the stop awaits the killed process's exit before returning, so the replacement core acquires the session lock without a `SessionLockedException` race

#### Scenario: Bounded wait cannot hang the stop
- **WHEN** the forced kill does not complete within the bounded exit-wait budget
- **THEN** the stop operation still returns rather than blocking indefinitely

