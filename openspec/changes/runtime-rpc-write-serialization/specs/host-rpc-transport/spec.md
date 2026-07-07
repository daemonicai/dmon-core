## MODIFIED Requirements

### Requirement: Framed JSONL transport over a core's stdio

`Dmon.Runtime` SHALL expose an `IRpcTransport` abstraction that owns the wire framing for a single core process and nothing above it. `SendAsync(Command, CancellationToken)` SHALL serialize the command as its polymorphic base `Command` type using the canonical `WireSerializerOptions.Default`, write the JSON followed by a single `\n`, and flush — with no `\r` ever emitted (strict LF framing, ADR-003). **`SendAsync` SHALL write each frame atomically and SHALL serialize concurrent callers, so that two or more overlapping `SendAsync` calls on the same transport can never interleave their bytes or terminators; each emitted line SHALL be one whole frame terminated by exactly one `\n`.** The transport SHALL expose the inbound stream as `IAsyncEnumerable<Event>`, deserializing each line with the same canonical options into the polymorphic `Event` base type. A default implementation SHALL be constructible from an `ICoreProcess` (reading its `StandardOutput`, writing its `StandardInput`).

#### Scenario: Command is framed with a single trailing LF
- **WHEN** a caller invokes `SendAsync` with a `Command`
- **THEN** the transport writes the command's canonical JSON followed by exactly one `\n`, contains no `\r`, and flushes the stream

#### Scenario: Concurrent sends do not interleave
- **WHEN** multiple callers invoke `SendAsync` concurrently on the same transport (for example a host whose event handlers dispatch sends without awaiting each other)
- **THEN** every frame is written whole and terminated by exactly one `\n`, no frame's bytes are interleaved with another's, and each `\n`-delimited line deserializes back into the exact `Command` that was sent

#### Scenario: Inbound lines surface as typed events
- **WHEN** the core writes a JSONL event line to stdout
- **THEN** the transport's `IAsyncEnumerable<Event>` yields the corresponding polymorphic `Event` subtype deserialized with `WireSerializerOptions.Default`
