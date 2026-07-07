## MODIFIED Requirements

### Requirement: Running-turn-aware detached lifetime
On detected detach, the network host SHALL start a grace timer. **Every detected disconnect SHALL arm the grace timer** — this includes an orderly connection-control `detach`, a heartbeat-detected dead connection, **and a drain/send failure on the forwarding path that clears the handler's current connection**: no code path may leave a handler with a cleared connection but an un-armed grace timer, since such a handler is never reaped. A **created-but-never-attached** handler (registered after a session `create` but never attached by a client) SHALL likewise be reapable after the idle TTL, with its reap clock cleared on the first successful `attach`. An idle detached handler (no turn in flight) SHALL be reaped after a configurable idle TTL. A detached handler with a turn in flight SHALL be retained until the turn completes, after which the idle TTL applies, bounded by a configurable absolute maximum. The number of concurrently live handlers SHALL be bounded by a configurable cap. On reap the network host SHALL terminate the handler's `dmoncore` process and release its per-session event-replay buffer.

#### Scenario: Idle detached handler reaped
- **WHEN** a handler has been detached with no turn in flight for longer than the idle TTL
- **THEN** the network host terminates its `dmoncore` process and removes it from the registry

#### Scenario: In-flight turn survives detach
- **WHEN** a connection drops while a turn is running
- **THEN** the handler is retained until the turn completes (bounded by the absolute maximum) rather than reaped at the idle TTL

#### Scenario: Drain-failure disconnect is reapable
- **WHEN** the forwarding path's send/pump to a connection fails and the handler clears that connection, and the client does not reattach
- **THEN** the handler's grace timer is armed (as for an orderly detach) and the handler is reaped after the idle TTL, terminating its `dmoncore` process and releasing its buffer, rather than leaking indefinitely

#### Scenario: Created-but-never-attached handler reaped
- **WHEN** a session is created and its handler registered, but no client ever attaches to it within the idle TTL
- **THEN** the handler is reaped — its `dmoncore` process terminated and it is removed from the registry — rather than leaking indefinitely

#### Scenario: Attach before the grace TTL keeps the session alive
- **WHEN** a client attaches to a created-but-never-attached handler (or reattaches to a drain-failure-detached handler) before the idle TTL elapses
- **THEN** the reap clock is cleared and the handler is retained, serving the connection normally
