## MODIFIED Requirements

### Requirement: Connection-control sub-protocol
The network host SHALL define connection-control frames layered around the unchanged ADR-003 messages: `attach` (client→host: `sessionId`, `lastSeq`), `attached` (host→client: `generation`, `headSeq`, `wire`), `ack` (host→client: command `id`), and `ping`/`pong` (both directions). The `attached` frame SHALL carry the `Major.Minor` wire protocol version the network host implements, so a client can establish compatibility on connect rather than discovering an incompatibility later as a malformed frame or an unrecognised event. The ADR-003 command/event wire shapes SHALL NOT be modified by this change.

#### Scenario: Attach acknowledged with generation and head
- **WHEN** a client sends an `attach` for an existing session
- **THEN** the network host replies `attached` carrying the new `generation` and the current `headSeq`

#### Scenario: ADR-003 shapes unchanged
- **WHEN** command and event frames are inspected on the wire
- **THEN** they are byte-compatible with the ADR-003 stdio shapes, and control frames are additive and distinguishable

#### Scenario: The wire version is advertised on attach
- **WHEN** the network host replies `attached`
- **THEN** the reply carries the `Major.Minor` wire protocol version the host implements, sourced from the single protocol-version constant rather than restated
