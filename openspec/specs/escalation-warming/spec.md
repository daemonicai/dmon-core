## Purpose

Define the daemon's `EscalationWarmingService`: an `ISessionActivityListener` that pre-starts the mlx escalation runtime on session activation and each turn (fire-and-forget, never blocking), refreshes an idle timer on every activity, and tears the runtime down via `StopAsync` after a configurable idle window to release memory. Warming is an optimization only — escalation correctness never depends on it, because the escalation request path itself ensures the runtime is running and the fixed port lets a cached client reconnect after a respawn.

## Requirements

### Requirement: Activity-driven escalation warming
The daemon SHALL provide an `EscalationWarmingService` that implements `ISessionActivityListener`. On `OnSessionActivated` or `OnTurnStarted` it SHALL trigger a fire-and-forget `EnsureRunningAsync` on the escalation runtime and reset its idle timer. Warming SHALL NOT block the session command or the turn.

#### Scenario: Warm on session activation
- **WHEN** a session is activated
- **THEN** the service triggers a non-blocking `EnsureRunningAsync` on the escalation runtime and resets the idle timer

#### Scenario: Warm and refresh on each turn
- **WHEN** a turn starts
- **THEN** the service ensures the escalation runtime is warming and resets the idle timer

#### Scenario: Warming never blocks the caller
- **WHEN** warming is in progress or the escalation server is still loading
- **THEN** the originating session command or turn proceeds without waiting

### Requirement: Idle-timeout teardown
The service SHALL maintain an idle timer reset by every activity notification. When the configured idle window elapses with no activity across all sessions, it SHALL call `StopAsync` on the escalation runtime to release its memory. The idle window SHALL be configurable with a sane default.

#### Scenario: Teardown after idle window
- **WHEN** no session activation or turn occurs for the configured idle window
- **THEN** the service calls `StopAsync` on the escalation runtime

#### Scenario: Activity cancels pending teardown
- **WHEN** activity occurs before the idle window elapses
- **THEN** the idle timer resets and no teardown happens

### Requirement: Warming is an optimization, not a correctness dependency
Escalation correctness SHALL NOT depend on warming having completed. Because the escalation request path itself ensures the runtime is running, an escalation that arrives before warming finishes (or just after an idle teardown) SHALL still succeed by spawning synchronously on that path, incurring only added latency for that turn.

#### Scenario: Escalation before warmup self-heals
- **WHEN** an escalation request arrives while the escalation runtime is stopped or still warming
- **THEN** the escalation path ensures the runtime is running and the request succeeds, with only added latency

#### Scenario: Fixed port lets the cached client reconnect after respawn
- **WHEN** the escalation runtime is torn down and later respawned on the same fixed port
- **THEN** a previously cached escalation client reconnects to the respawned server without reconfiguration
