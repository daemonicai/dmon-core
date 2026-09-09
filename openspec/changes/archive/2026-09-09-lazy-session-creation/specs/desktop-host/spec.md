## MODIFIED Requirements

### Requirement: `/reload` restarts the core between turns

The desktop host SHALL provide a reload action that restarts the core to re-read configuration: dispose the current `IRpcClient`, relaunch the core via `ICoreLauncher`/`ICoreProcess` (the Terminal restart pattern), rebind the event subscription to the fresh process, and re-open the active session directory. Reload SHALL run only between turns, never during an active streaming turn.

To make re-opening the active session possible at all, the host SHALL track **every** route by which a session becomes active, including a session the core creates on its own initiative and announces with `sessionStarted`. A host that tracks only sessions created by commands it issued has no active session to re-open, because the desktop host issues no session-creating command.

#### Scenario: Reload relaunches and rebinds

- **WHEN** the user triggers reload while idle
- **THEN** the previous core is stopped, a fresh core is launched, and the host consumes events from and sends commands to the new process

#### Scenario: Reload rejected during streaming

- **WHEN** reload is triggered during an active streaming turn
- **THEN** the restart does not occur until the turn completes

#### Scenario: Implicitly created session is re-opened on reload

- **WHEN** the user submits a turn with no active session, the core creates one and emits `sessionStarted`, and the user then triggers reload
- **THEN** the host has tracked that session as active and re-opens it against the fresh core, so the conversation continues in the same session directory rather than a second one being created by the next turn
