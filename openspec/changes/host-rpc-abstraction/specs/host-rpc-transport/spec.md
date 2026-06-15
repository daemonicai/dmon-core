## ADDED Requirements

### Requirement: Framed JSONL transport over a core's stdio

`Dmon.Runtime` SHALL expose an `IRpcTransport` abstraction that owns the wire framing for a single core process and nothing above it. `SendAsync(Command, CancellationToken)` SHALL serialize the command as its polymorphic base `Command` type using the canonical `WireSerializerOptions.Default`, write the JSON followed by a single `\n`, and flush — with no `\r` ever emitted (strict LF framing, ADR-003). The transport SHALL expose the inbound stream as `IAsyncEnumerable<Event>`, deserializing each line with the same canonical options into the polymorphic `Event` base type. A default implementation SHALL be constructible from an `ICoreProcess` (reading its `StandardOutput`, writing its `StandardInput`).

#### Scenario: Command is framed with a single trailing LF
- **WHEN** a caller invokes `SendAsync` with a `Command`
- **THEN** the transport writes the command's canonical JSON followed by exactly one `\n`, contains no `\r`, and flushes the stream

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
