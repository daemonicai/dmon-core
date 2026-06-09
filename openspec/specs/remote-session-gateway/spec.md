# remote-session-gateway Specification

## Purpose
TBD - created by archiving change remote-session-gateway. Update Purpose after archive.
## Requirements
### Requirement: WebSocket transport with near-verbatim forwarding
The `Dmon.Gateway` host SHALL expose a WebSocket endpoint that forwards ADR-003 command and event JSON between the client and a `dmoncore` process as text frames without reshaping the command/event payloads. Client frames SHALL be written to the core's stdin and core stdout events SHALL be sent to the currently attached connection. The gateway SHALL reuse `Dmon.Runtime`'s `CoreProcessManager` (ADR-011) to discover, spawn, and manage the core process.

#### Scenario: Command forwarded to the core
- **WHEN** a client sends an ADR-003 command frame over the WebSocket
- **THEN** the gateway writes the unchanged command JSON to the owning session's core stdin

#### Scenario: Event forwarded to the client
- **WHEN** the core emits an ADR-003 event on stdout and a connection is attached
- **THEN** the gateway sends the unchanged event JSON to that connection as a text frame

### Requirement: Session handler outlives the connection
A `SessionHandler` SHALL own exactly one `dmoncore` process for one `sessionId` and SHALL be held in an in-memory session registry keyed by `sessionId`. A WebSocket connection SHALL attach to a handler on connect and detach on disconnect; the handler SHALL persist across detach so a later reconnect re-attaches to the same running core. The stdio pump SHALL bind to the handler, not to any single connection.

#### Scenario: Handler survives a dropped connection
- **WHEN** an attached connection drops while its session exists
- **THEN** the `SessionHandler` and its `dmoncore` process remain alive and a subsequent `attach` with the same `sessionId` re-binds to them

#### Scenario: Events buffered while detached
- **WHEN** the core emits events while no connection is attached
- **THEN** the gateway durably retains them for later replay rather than discarding them

### Requirement: Connection-control sub-protocol
The gateway SHALL define connection-control frames layered around the unchanged ADR-003 messages: `attach` (client→gateway: `sessionId`, `lastSeq`), `attached` (gateway→client: `generation`, `headSeq`), `ack` (gateway→client: command `id`), and `ping`/`pong` (both directions). The ADR-003 command/event wire shapes SHALL NOT be modified by this change.

#### Scenario: Attach acknowledged with generation and head
- **WHEN** a client sends an `attach` for an existing session
- **THEN** the gateway replies `attached` carrying the new `generation` and the current `headSeq`

#### Scenario: ADR-003 shapes unchanged
- **WHEN** command and event frames are inspected on the wire
- **THEN** they are byte-compatible with the ADR-003 stdio shapes, and control frames are additive and distinguishable

### Requirement: Per-session event sequencing
The gateway SHALL assign a monotonic per-session sequence number (`seq`) to every server→client event as it flows out, and SHALL retain the sequenced events in the live handler's in-memory `seq`-indexed buffer (ADR-014; the core's `messages.jsonl` is the conversational store, written only by the core, and is not the event-stream backing). The `headSeq` reported at attach SHALL reflect the highest assigned `seq` for the session.

#### Scenario: Monotonic sequence
- **WHEN** the core emits a series of events for a session
- **THEN** each is assigned a strictly increasing `seq`, with no gaps or reuse within the session

### Requirement: Replay on reattach
On `attach`, the gateway SHALL deliver every event with `seq` greater than the client's `lastSeq` up to `headSeq`, then resume live delivery. The gateway SHALL subscribe to the live stream before replaying history from the handler's retained `seq`-indexed buffer (ADR-014) and dedupe by `seq`, so events arriving during replay are neither dropped nor duplicated.

#### Scenario: Missed events replayed in order
- **WHEN** a client reattaches with `lastSeq = N` and the session's `headSeq = M` (M > N)
- **THEN** the client receives events `N+1 … M` in order before any newer live event

#### Scenario: No duplicate across the replay seam
- **WHEN** an event is emitted while replay is in progress
- **THEN** the client receives that event exactly once

### Requirement: Command idempotency across reconnects
The gateway SHALL acknowledge each received command by its ADR-003 `id`. A reattaching client MAY resend commands it has not seen acked; the gateway SHALL dedupe by `id` so a command delivered to the core before a drop is not executed twice.

#### Scenario: Resent command deduped
- **WHEN** a client resends a command whose `id` the gateway has already accepted for that session
- **THEN** the gateway does not deliver it to the core a second time and re-acks the `id`

### Requirement: Stale-connection fencing and single active writer
Each `attach` SHALL be issued a strictly increasing `generation` token. The gateway SHALL drop and close any connection whose generation is older than the handler's current generation. A new attach to a live handler SHALL evict (fence and close) the prior connection, so a session has a single active writer.

#### Scenario: Older generation fenced
- **WHEN** a frame arrives on a connection whose generation is less than the handler's current generation
- **THEN** the gateway ignores the frame and closes that connection

#### Scenario: New attach evicts the prior connection
- **WHEN** a new `attach` succeeds for a session that already has an attached connection
- **THEN** the prior connection is closed and only the new connection is active

### Requirement: Running-turn-aware detached lifetime
On detected detach, the gateway SHALL start a grace timer. An idle detached handler (no turn in flight) SHALL be reaped after a configurable idle TTL. A detached handler with a turn in flight SHALL be retained until the turn completes, after which the idle TTL applies, bounded by a configurable absolute maximum. The number of concurrently live handlers SHALL be bounded by a configurable cap.

#### Scenario: Idle detached handler reaped
- **WHEN** a handler has been detached with no turn in flight for longer than the idle TTL
- **THEN** the gateway terminates its `dmoncore` process and removes it from the registry

#### Scenario: In-flight turn survives detach
- **WHEN** a connection drops while a turn is running
- **THEN** the handler is retained until the turn completes (bounded by the absolute maximum) rather than reaped at the idle TTL

### Requirement: Heartbeat liveness
The gateway SHALL operate application-level `ping`/`pong` heartbeats on a configurable interval to keep the connection alive across carrier NAT idle-timeouts and to detect dead connections. The detached-lifetime grace timer SHALL begin on *detected* disconnect.

#### Scenario: Dead connection detected and detached
- **WHEN** heartbeats are not answered within the configured interval
- **THEN** the gateway treats the connection as detached and starts the detached-lifetime grace timer

### Requirement: Permission prompts park while detached
While a session is detached, a turn that reaches an ADR-006 permission gate SHALL park: the gateway SHALL neither auto-approve nor auto-deny the request. On reattach the parked request SHALL be delivered to the client; if the handler is reaped under its TTL before reattach, the parked request SHALL be abandoned with the turn.

#### Scenario: Prompt withheld while detached
- **WHEN** a turn reaches a permission gate and no connection is attached
- **THEN** the request is held unresolved and no approval or denial is applied

#### Scenario: Parked prompt delivered on reattach
- **WHEN** a client reattaches while a permission request is parked
- **THEN** the gateway delivers that request to the client for resolution

### Requirement: Tailscale-fronted authentication
The gateway SHALL bind to loopback by default and SHALL NOT listen on a public network interface. Transport encryption, device identity, and per-device revocation SHALL be delegated to Tailscale (`tailscale serve` fronts the loopback gateway with a Let's Encrypt certificate for the MagicDNS name). The gateway MAY additionally require a single pre-shared key presented as `Authorization: Bearer <key>` on the WebSocket upgrade; when configured, a missing or mismatched key SHALL reject the upgrade with HTTP 401 before any socket is opened.

#### Scenario: Loopback bind by default
- **WHEN** the gateway starts with no explicit bind address
- **THEN** it listens only on loopback

#### Scenario: Shared key enforced on upgrade
- **WHEN** a shared key is configured and a WebSocket upgrade arrives without a matching `Authorization: Bearer` value
- **THEN** the upgrade is rejected with HTTP 401 and no WebSocket is established

### Requirement: Profile-selecting session creation
Session creation SHALL allocate a `sessionId`, select an agent profile (the `agent-profiles` capability — persona, asset directory, permission mode), provision the per-session storage directory (ADR-004), and spawn the `SessionHandler`. A requested profile that does not exist SHALL fail session creation with an actionable error.

#### Scenario: Session created under a profile
- **WHEN** a client creates a session specifying a profile
- **THEN** the gateway spawns a handler whose core runs under that profile and returns the new `sessionId`

#### Scenario: Unknown profile fails creation
- **WHEN** session creation requests a profile that is not in the effective set
- **THEN** creation fails with an actionable error and no handler is spawned

### Requirement: Single server instance for V1
The session registry SHALL be in-process and `sessionId → SessionHandler` affinity SHALL be local to the single gateway instance. The gateway SHALL NOT attempt to migrate a live session to another instance or share the registry across instances in V1.

#### Scenario: Reattach requires the owning instance
- **WHEN** a client reattaches with a `sessionId`
- **THEN** the attach is served by the same gateway instance that holds that session's handler

### Requirement: Gateway session-create control frame
The gateway WebSocket surface SHALL accept a `create` control frame carrying an optional `profile`, in addition to the existing `attach` frame. On a successful create the gateway SHALL spawn a `dmoncore` process, drive it to create a session under the requested profile, register the resulting `SessionHandler`, and reply with a typed `created` frame carrying the new `sessionId` (ADR-015 — a typed, correlated result, not a generic response envelope). The client SHALL then `attach` to that `sessionId` through the existing attach flow. A create frame SHALL be valid as a first frame on a connection, alongside `attach`.

#### Scenario: Create spawns a profile-bound session
- **WHEN** a client sends `create {profile: "researcher"}` and `researcher` is in the effective profile set
- **THEN** the gateway spawns a core, creates a session whose record stores `profile` = `"researcher"`, registers the handler, and replies `created {sessionId}`

#### Scenario: Create without a profile
- **WHEN** a client sends `create {}` with no profile
- **THEN** the gateway spawns a core, creates a session with no stored profile (resolving to the default), registers the handler, and replies `created {sessionId}`

#### Scenario: Client attaches to the created session
- **WHEN** the client has received `created {sessionId}`
- **THEN** sending `attach {sessionId, lastSeq: 0}` attaches to the spawned handler through the existing attach flow

### Requirement: Pre-spawn profile validation rejects unknown profiles
The gateway SHALL validate the requested profile against the effective profile set **before** spawning any core process. A requested profile that is not in the effective set SHALL be rejected with a typed, actionable error naming the unknown profile, and SHALL NOT spawn a core, SHALL NOT register a handler, and SHALL NOT consume a slot against the concurrent-handler cap. Validation at the gateway is an early-rejection convenience; the core's own first-turn resolution remains authoritative.

#### Scenario: Unknown profile rejected without spawning
- **WHEN** a client sends `create {profile: "nope"}` and `nope` is not in the effective profile set
- **THEN** the gateway replies with an actionable error naming `nope`, spawns no core, and registers no handler

#### Scenario: No handler leaked on rejection
- **WHEN** a create is rejected for an unknown profile
- **THEN** the registry handler count is unchanged and no orphaned core process remains

### Requirement: Cap-enforced create registration
The gateway SHALL register a newly created session's handler under the concurrent-handler cap (`MaxConcurrentHandlers`) using the cap-enforcing registration primitive. When the cap is already reached, create SHALL fail with a typed, actionable error and SHALL tear down any core it spawned for that create, leaving no orphaned process and no registry entry. Reattaching to an existing session SHALL NOT be subject to the cap.

#### Scenario: Create rejected at the cap
- **WHEN** the registry already holds `MaxConcurrentHandlers` handlers and a client sends a valid `create`
- **THEN** the gateway replies with an actionable cap error, the spawned core (if any) is torn down, and no new handler is registered

#### Scenario: Reattach is exempt from the cap
- **WHEN** the cap is reached and a client `attach`es to an already-registered session
- **THEN** the attach succeeds, since reattach does not allocate a new handler

