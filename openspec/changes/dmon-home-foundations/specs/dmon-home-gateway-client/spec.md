## ADDED Requirements

### Requirement: The host reaches its session through the gateway, not through stdio

The host SHALL obtain and drive its session by connecting to the network host (`Dmon.Network`) as a client, using the same connection-control sub-protocol a remote client uses. The host SHALL NOT speak the ADR-003 stdio protocol to a core process directly, and SHALL NOT spawn core processes itself.

#### Scenario: Sessions are reached over the gateway

- **WHEN** the host establishes a session
- **THEN** it does so over a WebSocket connection to the network host
- **AND** it spawns no core process of its own

### Requirement: The transport is abstracted behind a protocol

The WebSocket transport SHALL sit behind a transport abstraction, and no code above that abstraction SHALL reference a transport-specific type. This exists so a direct stdio core can be substituted later without changing callers.

#### Scenario: Callers are transport-agnostic

- **WHEN** the gateway client's code above the transport abstraction is inspected
- **THEN** it references only the transport abstraction, not any concrete WebSocket type

#### Scenario: A substitute transport drives the client

- **WHEN** a test substitutes an in-memory transport conforming to the abstraction
- **THEN** the handshake, turn submission and event rendering are exercised without a network connection

### Requirement: Frames are routed by the gateway discriminator

The client SHALL route an incoming frame carrying a `gw` field as a connection-control frame, and a frame without one as an ADR-003 event. It SHALL encode and decode the control frames `attach`, `attached`, `ack`, `create`, `created`, `createRejected`, `ping` and `pong`. Serialisation SHALL be camelCase, SHALL tolerate a discriminator appearing at any position in the object, and SHALL omit null fields.

#### Scenario: A control frame is routed as control

- **WHEN** a frame carrying a `gw` field arrives
- **THEN** the client decodes it as the corresponding connection-control frame

#### Scenario: An ADR-003 event is routed as an event

- **WHEN** a frame with no `gw` field arrives
- **THEN** the client decodes it as an ADR-003 event and does not treat it as a control frame

#### Scenario: Discriminator position is tolerated

- **WHEN** a frame arrives whose discriminator field is not the first member of the object
- **THEN** the client decodes it correctly

#### Scenario: Liveness is answered

- **WHEN** the network host sends a `ping`
- **THEN** the client replies with a `pong`

### Requirement: A session is established by create then attach

The client SHALL establish a session by sending `create`, awaiting `created`, and then sending `attach` with the returned session id. It SHALL surface a `createRejected` reply as an actionable error distinguishing the rejection code from an ADR-003 error event.

#### Scenario: Create is followed by attach

- **WHEN** the client creates a session
- **THEN** it awaits `created` and then attaches using the session id from that reply
- **AND** it records the `generation` and `headSeq` from the `attached` reply

#### Scenario: A rejected create is surfaced

- **WHEN** the network host replies `createRejected`
- **THEN** the client surfaces the rejection code and message as an actionable error and does not attempt to attach

### Requirement: Reattach resumes from the last observed sequence

On reconnecting to an existing session the client SHALL attach with the highest event sequence number it has already observed, so the network host replays only what was missed. The client SHALL render replayed events without duplicating events it has already rendered.

#### Scenario: Missed events are replayed

- **WHEN** the client reattaches after a dropped connection having observed events up to a sequence number
- **THEN** it sends that sequence number as its last-seen value and renders the replayed events that follow it

#### Scenario: No duplicate across the replay seam

- **WHEN** replayed events are followed by live events
- **THEN** no event is rendered twice

### Requirement: Turns are submitted and streamed replies rendered incrementally

The client SHALL submit a turn as an ADR-003 command carrying a unique id, and SHALL render streamed message deltas as they arrive, completing the rendered turn when the turn-end event arrives.

#### Scenario: A turn streams to completion

- **WHEN** the client submits a turn
- **THEN** it renders each message delta as it arrives and marks the turn complete on the turn-end event

#### Scenario: Commands carry unique ids

- **WHEN** the client submits any command
- **THEN** that command carries an id unique within the session, so the network host can acknowledge and deduplicate it

### Requirement: Wire protocol compatibility is checked on connect

The client SHALL declare the wire protocol version it implements and SHALL treat itself as compatible with the core only when the major and minor components match. An incompatibility SHALL be surfaced as a clear, actionable error rather than a silent failure or a malformed-frame error.

#### Scenario: Matching versions connect

- **WHEN** the negotiated wire version's major and minor components match the client's
- **THEN** the client proceeds

#### Scenario: A mismatched version is surfaced

- **WHEN** the negotiated wire version's major or minor component differs from the client's
- **THEN** the client refuses to proceed and surfaces the version mismatch, naming both versions

### Requirement: The client authenticates with a device key when the store requires it

The client SHALL present a device key when connecting. When the network host's device-key store is empty or absent on a loopback bind, authentication is disabled and the client SHALL connect without a key. When a key is required, the client SHALL hold its own device credential — distinct from any other client's — with the secret stored in the system Keychain and never written to the host's own configuration or logs.

#### Scenario: Loopback with no configured devices connects unauthenticated

- **WHEN** the network host's device-key store is empty or absent and the bind is loopback
- **THEN** the client connects without presenting a key

#### Scenario: A configured store requires the host's own key

- **WHEN** the network host's device-key store is populated
- **THEN** the client presents its own device key, distinct from any other client's credential

#### Scenario: The secret is not exposed

- **WHEN** the host's configuration files and logs are inspected
- **THEN** the device key secret does not appear in either
