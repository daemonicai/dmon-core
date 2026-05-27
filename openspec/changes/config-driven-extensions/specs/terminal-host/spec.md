## ADDED Requirements

### Requirement: /reload restarts the core to re-read config
The terminal host SHALL provide a `/reload` command that restarts the core process via `CoreProcessManager.RestartAsync`: stop the current core, spawn a fresh one, re-bind the host's stdio read/write loop to the new process's standard output/input, and re-open the active session directory so the conversation continues. The fresh core re-reads `config.yaml` and loads the effective extension set. `/reload` SHALL run only between turns, never during an active streaming call.

#### Scenario: /reload spawns a fresh core and re-binds stdio
- **WHEN** the user issues `/reload` while idle
- **THEN** the previous core process is stopped and a new one is started
- **AND** the host reads subsequent events from the new process's standard output and writes commands to its standard input

#### Scenario: Session survives /reload
- **WHEN** `/reload` is issued while a session is active
- **THEN** the new core re-opens the same session directory
- **AND** the prior conversation history remains available

#### Scenario: Config changes take effect after /reload
- **WHEN** the user adds or removes an extension in `config.yaml` and then issues `/reload`
- **THEN** the effective extension set on the fresh core reflects the edited config

#### Scenario: /reload is rejected during streaming
- **WHEN** `/reload` is issued during an active streaming turn
- **THEN** the restart does not occur until the turn completes
