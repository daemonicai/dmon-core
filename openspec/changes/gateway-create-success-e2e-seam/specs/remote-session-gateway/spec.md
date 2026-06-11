## ADDED Requirements

### Requirement: Create-handshake results are excluded from the event stream

During `create`, the gateway SHALL fully consume the `session.create` and `session.load`
handshake result events (`session.createResult`, `session.loadResult`) before the session's
`SessionHandler` begins assigning per-session sequence numbers (ADR-014). As a consequence,
those handshake result events SHALL NOT be assigned a `seq`, SHALL NOT enter the handler's
`seq`-indexed buffer, and SHALL NOT be replayed to any client on attach. The first event the
handler sequences for a session SHALL be a post-handshake event, never `session.createResult`
or `session.loadResult`.

#### Scenario: Handshake results never reach the seq stream

- **WHEN** the gateway handles a `create` control frame, drives the `session.create` →
  path-less `session.load` handshake to success, registers the resulting `SessionHandler`, and
  then forwards subsequent core events to an attached client
- **THEN** no event carrying `seq` is a `session.createResult` or `session.loadResult`; the
  lowest `seq` assigned for the session corresponds to the first event emitted after the
  handshake completed

#### Scenario: Create success reports the new session id

- **WHEN** the gateway handles a `create` control frame and the core completes the handshake
- **THEN** the gateway replies with a typed `created` frame carrying the `sessionId` returned by
  the core, and the session's handler is registered and attachable under that `sessionId`
