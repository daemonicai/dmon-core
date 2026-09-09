## ADDED Requirements

### Requirement: Session start is surfaced to the user

The console host SHALL make the start of a session visible to the user, whichever way the session came into being. A session becoming active SHALL always produce a visible indication carrying the session's identity; the host SHALL NOT track a new session silently.

The host SHALL surface an explicitly created session (`/new` → `session.createResult`) and an implicitly created one (`sessionStarted`) through the **same** display path, so the two cannot drift apart in wording or in whether they appear at all.

#### Scenario: Explicit session creation is displayed

- **WHEN** the user types `/new` and the core responds with `session.createResult`
- **THEN** the host displays the new session context, identifying the session

#### Scenario: Implicit session creation is displayed

- **WHEN** the user submits a turn with no active session and the core emits `sessionStarted`
- **THEN** the host displays the new session context in the same form as for `/new`, so the user learns a session was started on their behalf

#### Scenario: Session identity is visible, not merely tracked

- **WHEN** any event that makes a session active is handled
- **THEN** the host both records the active session id and produces a user-visible indication of it; recording it without displaying anything is not sufficient
