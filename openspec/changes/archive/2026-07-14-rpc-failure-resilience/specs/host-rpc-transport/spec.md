## ADDED Requirements

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
