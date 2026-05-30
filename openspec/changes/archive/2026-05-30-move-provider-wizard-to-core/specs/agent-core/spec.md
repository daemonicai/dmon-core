## ADDED Requirements

### Requirement: Dispatch loop does not block on long-running commands
The agent core's command dispatch SHALL NOT block the stdin reader on a command that suspends awaiting a later host command. Long-running interactive commands — `turn.submit` and `wizard.start` — SHALL be dispatched on a tracked background task so the reader continues consuming stdin and can route the commands that resolve a suspended operation (`tool.confirmResponse`, `ui.inputResponse`, `wizard.answer`, `turn.abort`). Short, non-interactive commands SHALL continue to be processed inline. This conforms to ADR-003's "commands are fire-and-forget; the core suspends the turn until the response arrives". Errors raised by a backgrounded command SHALL be surfaced as an `error` event and SHALL NOT be silently dropped, and outstanding background tasks SHALL be observable at shutdown.

#### Scenario: Tool-confirmation round-trip completes over the real loop
- **WHEN** the host sends `turn.submit`, the core emits `tool.confirmRequest`, and the host then sends `tool.confirmResponse` as a subsequent line
- **THEN** the reader reads and routes the `tool.confirmResponse` while the turn is suspended, the turn resumes, and `turnEnd` is emitted (no deadlock)

#### Scenario: Wizard round-trip completes over the real loop
- **WHEN** the host sends `wizard.start`, the core emits a `wizard.step` event, and the host then sends `wizard.answer` as a subsequent line
- **THEN** the reader reads and routes the `wizard.answer` while the wizard is suspended, the wizard advances, and a further `wizard.step` or `providerConfigured` event is emitted

#### Scenario: Reader stays responsive during a long turn
- **WHEN** a `turn.submit` is in progress and the host sends `turn.abort`
- **THEN** the reader reads and routes `turn.abort` without waiting for the turn to finish, and the turn is cancelled

#### Scenario: Backgrounded command error is surfaced
- **WHEN** a backgrounded long-running command throws
- **THEN** the core emits an `error` event describing the failure rather than dropping it silently
