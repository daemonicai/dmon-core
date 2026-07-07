## ADDED Requirements

### Requirement: Concurrent-safe start
`EnsureRunningAsync()` SHALL be safe under concurrent invocation. When multiple callers invoke it concurrently for the same runtime while no server is running, exactly **one** server process SHALL be spawned and environment provisioning SHALL occur at most **once**. Callers SHALL serialize on the check-then-spawn critical section and re-check liveness inside the serialized region, so that no caller spawns a second process on the runtime's fixed port and no spawned server process is left orphaned (unreachable by `StopAsync()`/`Dispose()`). This concurrency guarantee SHALL compose with the existing attach-first, idempotent lifecycle: a caller arriving while another caller's spawn is in progress SHALL wait and then attach to the resulting server rather than spawning its own.

#### Scenario: Concurrent cold-start spawns exactly one server
- **WHEN** two or more callers invoke `EnsureRunningAsync()` concurrently for the same runtime and no server is yet running
- **THEN** exactly one `mlx_lm.server` process is spawned, the remaining callers attach to it, and no orphaned process remains after all calls complete

#### Scenario: Environment provisioning runs once under concurrency
- **WHEN** concurrent callers trigger first-time `uv` environment provisioning for the same runtime
- **THEN** the venv is provisioned once, not concurrently against the same venv

#### Scenario: The retained process handle references the live server
- **WHEN** concurrent callers complete a cold start
- **THEN** the retained server-process handle references the single spawned server, so `StopAsync()`/`Dispose()` can terminate it with no process left running
