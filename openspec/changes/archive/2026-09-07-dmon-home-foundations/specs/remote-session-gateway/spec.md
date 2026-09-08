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

## ADDED Requirements

### Requirement: Close-code contract for host-initiated termination
When the network host closes a connection itself, the close SHALL use one of a fixed, documented set of WebSocket close codes chosen by failure class, so a client can distinguish the cause without inspecting server-side state. The application-range codes (4400, 4404, 4409, 4500) and the RFC 6455 standard code reused for oversize messages (1009) are part of the wire contract, not an implementation detail, and SHALL NOT be repurposed to mean something else without a corresponding spec update. Not every connection termination is a coded close of this kind — see the uncoded-termination scenario below, which the Heartbeat liveness requirement's own detection path produces. The create-phase paths that end the transport uncoded too — a `createRejected` reply for `core_timeout`, `cap_reached`, or `unknown_agent`, and a `created` reply on success — are each preceded by that explanatory reply frame on the data channel, so no diagnostic gap exists there. Where a coded close is available, it is the only observable signal of `AckFrame`'s never-silently-dropped guarantee (Command idempotency across reconnects): 4500 is the coded signal for a write failure. But resend-on-reconnect is the durable, safe response either way — coded close or uncoded drop — which is the guarantee a client should actually rely on, not an assumption that every failure is diagnosable from the wire.

#### Scenario: Protocol violation closes with 4400
- **WHEN** the first frame on a connection is neither `attach` nor `create`, or a first frame fails to parse as its declared kind, or the client sends a binary WebSocket message
- **THEN** the network host closes the connection with 4400 and a reason string naming the specific violation

#### Scenario: Unknown session closes with 4404
- **WHEN** a client sends `attach` naming a `sessionId` the network host has no handler for
- **THEN** the network host closes the connection with 4404

#### Scenario: Superseded connection closes with 4409
- **WHEN** a frame arrives on a connection whose generation is older than its handler's current generation, because a newer `attach` to the same session has taken over
- **THEN** the network host closes that connection with 4409
- **AND** the client SHOULD treat the closed connection as evicted rather than retry on it — a fresh `attach` opens a new, current connection

#### Scenario: Core spawn or handshake failure during create closes with 4500
- **WHEN** an unexpected failure occurs while spawning the core process or driving the `session.create`/`session.load` handshake in response to a `create` frame
- **THEN** the network host tears down the spawned core and closes the connection with 4500, without a `created` reply

#### Scenario: A write failure on an established session closes with 4500
- **WHEN** forwarding a command to an already-attached session's core process fails
- **THEN** the network host closes the connection with 4500 without acknowledging that command
- **AND** the client SHOULD resend the command after reconnecting — command idempotency (Command idempotency across reconnects) makes the resend safe whether or not the original reached the core

#### Scenario: Oversized message closes with the standard 1009
- **WHEN** an inbound message exceeds the network host's message-size ceiling
- **THEN** the network host closes the connection with the RFC 6455 standard code 1009, not an application-range code

#### Scenario: A connection can end with no close code at all
- **WHEN** the network host detects a dead connection through the Heartbeat liveness path rather than through an invalid frame or a failed write
- **THEN** the connection may end without any close code being sent
- **AND** the client SHOULD treat an uncoded drop as a possible outcome on any connection, not only a recognised or unrecognised coded close, and respond the same way it would to a coded close it cannot diagnose: reattach with its current `lastSeq` rather than assume every termination carries a diagnosable cause
