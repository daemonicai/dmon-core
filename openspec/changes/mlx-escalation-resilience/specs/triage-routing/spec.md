## ADDED Requirements

### Requirement: Backend resolution recovers from a transient fault
`TriageRouter` SHALL cache each backend client's **successful** resolution once, but SHALL NOT permanently cache a **faulted** resolution attempt. When a backend's resolution (the ADR-032 factory delegate, which performs the provider `EnsureRunningAsync` I/O) throws or faults on a given turn, a subsequent turn requiring that backend SHALL re-attempt resolution rather than re-throwing the previously cached fault. Resolution SHALL remain lazy and I/O-free at construction (ADR-027 D1 / ADR-032 D3): it is triggered by the first turn that needs the backend, not at build time. Concurrent first uses of the same backend SHALL still resolve it at most once while it is succeeding.

#### Scenario: Transient first-use resolution fault recovers on a later turn
- **WHEN** a backend's resolution faults on its first use (for example, the provider's `EnsureRunningAsync` throws a transient error) and a later turn needs that backend
- **THEN** the router re-attempts resolution for that backend and, if it now succeeds, dispatches the turn to it

#### Scenario: Successful resolution is cached
- **WHEN** a backend resolves successfully
- **THEN** subsequent turns reuse the cached client without re-resolving it

#### Scenario: Construction performs no I/O
- **WHEN** the `TriageRouter` is constructed
- **THEN** no backend factory is invoked and no provider I/O occurs until the first turn that needs a backend

## MODIFIED Requirements

### Requirement: Escalation backend is a fixed-port mlx runtime
The daemon's escalation backend SHALL be the mlx escalation runtime served on a fixed port. The escalation backend's lazily-resolved client MAY be cached for the daemon's lifetime; because the runtime always returns to the same fixed port after a teardown/respawn cycle, a cached escalation client SHALL remain valid across idle teardown and subsequent respawn. Before the escalation client produces output for a turn, the escalation runtime SHALL be ensured running (respawned on demand if it was torn down for idle or is otherwise not listening), rather than the turn being dispatched to a dead endpoint. This ensure-running check SHALL be attach-first, so it adds no meaningful latency when the runtime is already healthy. The requirement constrains the effect, not the seam: the ensure-running MAY be realized by the escalation client itself or by the request path that dispatches to it.

#### Scenario: Cached escalation client survives a respawn
- **WHEN** the escalation runtime is torn down for idle and later respawned on its fixed port
- **THEN** the previously resolved escalation client connects to the respawned server without being rebuilt

#### Scenario: Escalation request after idle teardown respawns the runtime
- **WHEN** the escalation runtime has been torn down for idle and an escalation is decided on the request path
- **THEN** the runtime is ensured running (respawned) before the escalation client produces output, and the turn completes against the respawned server rather than failing against a dead endpoint

#### Scenario: Healthy escalation runtime adds no respawn latency
- **WHEN** an escalation is decided and the escalation runtime is already running
- **THEN** the ensure-running check attaches to the live runtime without spawning a new process
