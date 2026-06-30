## ADDED Requirements

### Requirement: Escalation backend is a fixed-port mlx runtime
The daemon's escalation backend SHALL be the mlx escalation runtime served on a fixed port. The escalation backend's lazily-resolved client MAY be cached for the daemon's lifetime; because the runtime always returns to the same fixed port after a teardown/respawn cycle, a cached escalation client SHALL remain valid across idle teardown and subsequent respawn.

#### Scenario: Cached escalation client survives a respawn
- **WHEN** the escalation runtime is torn down for idle and later respawned on its fixed port
- **THEN** the previously resolved escalation client connects to the respawned server without being rebuilt

### Requirement: Warming integrates without changing routing semantics
Activity-driven warming of the escalation runtime SHALL be additive to routing: it pre-starts the escalation runtime but SHALL NOT change when escalation is decided or which backend handles a turn. The escalation request path SHALL continue to ensure the runtime is running, so routing remains correct whether or not warming has completed.

#### Scenario: Warming does not alter escalation decisions
- **WHEN** warming has pre-started the escalation runtime
- **THEN** the first-line→`think_harder`→escalation decision flow is unchanged; warming only removes start-up latency at hand-off

#### Scenario: Routing correct without warming
- **WHEN** warming has not run and an escalation is decided
- **THEN** the escalation path ensures the runtime is running and the turn completes correctly
