## ADDED Requirements

### Requirement: In-process session activity listener seam
The core SHALL define an in-process notification seam `ISessionActivityListener` with `OnSessionActivated(sessionId)` and `OnTurnStarted(sessionId)`. The seam SHALL be DI-discovered (zero or more registered listeners) and SHALL carry no policy — it conveys only that a session became active or a turn began. It SHALL NOT be exposed on the RPC/gateway wire.

#### Scenario: Listeners resolved from DI
- **WHEN** the core composes its services
- **THEN** all registered `ISessionActivityListener` implementations are discovered and invoked on activity, and zero registrations is valid (no-op)

#### Scenario: Seam is in-process only
- **WHEN** session activity occurs
- **THEN** listeners are notified in-process and no new event is emitted on the RPC/gateway wire

### Requirement: SessionHandler fires session activation
`SessionHandler` SHALL invoke `OnSessionActivated` for each registered listener when a session is created or loaded (where it already emits `SessionCreated`/`SessionLoaded`). Listener invocation SHALL NOT block or fail the session command — exceptions from a listener are isolated.

#### Scenario: Activation fired on create and load
- **WHEN** a session is created or loaded
- **THEN** every registered listener's `OnSessionActivated` is invoked with the session id

#### Scenario: Listener failure does not break session creation
- **WHEN** a listener throws during `OnSessionActivated`
- **THEN** the session create/load still succeeds and the failure is isolated

### Requirement: TurnHandler fires turn start
`TurnHandler` SHALL invoke `OnTurnStarted` for each registered listener at the start of each turn. Invocation SHALL NOT block or fail the turn — exceptions from a listener are isolated.

#### Scenario: Turn start fired per turn
- **WHEN** a turn begins
- **THEN** every registered listener's `OnTurnStarted` is invoked with the session id

#### Scenario: Listener failure does not break the turn
- **WHEN** a listener throws during `OnTurnStarted`
- **THEN** the turn proceeds normally and the failure is isolated
