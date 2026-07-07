# remote-session-gateway Specification

## Purpose
TBD - created by archiving change remote-session-gateway. Update Purpose after archive.
## Requirements
### Requirement: WebSocket transport with near-verbatim forwarding
The `Dmon.Network` host SHALL expose a WebSocket endpoint that forwards ADR-003 command and event JSON between the client and a `dmoncore` process as text frames without reshaping the command/event payloads. Client frames SHALL be written to the core's stdin and core stdout events SHALL be sent to the currently attached connection. The network host SHALL reuse `Dmon.Runtime`'s `CoreProcessManager` (ADR-011) to discover, spawn, and manage the core process.

#### Scenario: Command forwarded to the core
- **WHEN** a client sends an ADR-003 command frame over the WebSocket
- **THEN** the network host writes the unchanged command JSON to the owning session's core stdin

#### Scenario: Event forwarded to the client
- **WHEN** the core emits an ADR-003 event on stdout and a connection is attached
- **THEN** the network host sends the unchanged event JSON to that connection as a text frame

### Requirement: Session handler outlives the connection
A `SessionHandler` SHALL own exactly one `dmoncore` process for one `sessionId` and SHALL be held in an in-memory session registry keyed by `sessionId`. A WebSocket connection SHALL attach to a handler on connect and detach on disconnect; the handler SHALL persist across detach so a later reconnect re-attaches to the same running core. The stdio pump SHALL bind to the handler, not to any single connection.

#### Scenario: Handler survives a dropped connection
- **WHEN** an attached connection drops while its session exists
- **THEN** the `SessionHandler` and its `dmoncore` process remain alive and a subsequent `attach` with the same `sessionId` re-binds to them

#### Scenario: Events buffered while detached
- **WHEN** the core emits events while no connection is attached
- **THEN** the network host durably retains them for later replay rather than discarding them

### Requirement: Connection-control sub-protocol
The network host SHALL define connection-control frames layered around the unchanged ADR-003 messages: `attach` (client→host: `sessionId`, `lastSeq`), `attached` (host→client: `generation`, `headSeq`), `ack` (host→client: command `id`), and `ping`/`pong` (both directions). The ADR-003 command/event wire shapes SHALL NOT be modified by this change.

#### Scenario: Attach acknowledged with generation and head
- **WHEN** a client sends an `attach` for an existing session
- **THEN** the network host replies `attached` carrying the new `generation` and the current `headSeq`

#### Scenario: ADR-003 shapes unchanged
- **WHEN** command and event frames are inspected on the wire
- **THEN** they are byte-compatible with the ADR-003 stdio shapes, and control frames are additive and distinguishable

### Requirement: Per-session event sequencing
The network host SHALL assign a monotonic per-session sequence number (`seq`) to every server→client event as it flows out, and SHALL retain the sequenced events in the live handler's in-memory `seq`-indexed buffer (ADR-014; the core's `messages.jsonl` is the conversational store, written only by the core, and is not the event-stream backing). The `headSeq` reported at attach SHALL reflect the highest assigned `seq` for the session.

#### Scenario: Monotonic sequence
- **WHEN** the core emits a series of events for a session
- **THEN** each is assigned a strictly increasing `seq`, with no gaps or reuse within the session

### Requirement: Replay on reattach
On `attach`, the network host SHALL deliver every event with `seq` greater than the client's `lastSeq` up to `headSeq`, then resume live delivery. The network host SHALL subscribe to the live stream before replaying history from the handler's retained `seq`-indexed buffer (ADR-014) and dedupe by `seq`, so events arriving during replay are neither dropped nor duplicated.

#### Scenario: Missed events replayed in order
- **WHEN** a client reattaches with `lastSeq = N` and the session's `headSeq = M` (M > N)
- **THEN** the client receives events `N+1 … M` in order before any newer live event

#### Scenario: No duplicate across the replay seam
- **WHEN** an event is emitted while replay is in progress
- **THEN** the client receives that event exactly once

### Requirement: Command idempotency across reconnects
The network host SHALL acknowledge each received command by its ADR-003 `id`. A reattaching client MAY resend commands it has not seen acked; the network host SHALL dedupe by `id` so a command delivered to the core before a drop is not executed twice.

#### Scenario: Resent command deduped
- **WHEN** a client resends a command whose `id` the network host has already accepted for that session
- **THEN** the network host does not deliver it to the core a second time and re-acks the `id`

### Requirement: Stale-connection fencing and single active writer
Each `attach` SHALL be issued a strictly increasing `generation` token. The network host SHALL drop and close any connection whose generation is older than the handler's current generation. A new attach to a live handler SHALL evict (fence and close) the prior connection, so a session has a single active writer. Revocation of a device key SHALL additionally trigger this fencing: when a key transitions to revoked, the network host SHALL close any currently-attached connection tagged with that `keyId`, across all sessions, reusing the same evict-and-close path.

#### Scenario: Older generation fenced
- **WHEN** a frame arrives on a connection whose generation is less than the handler's current generation
- **THEN** the network host ignores the frame and closes that connection

#### Scenario: New attach evicts the prior connection
- **WHEN** a new `attach` succeeds for a session that already has an attached connection
- **THEN** the prior connection is closed and only the new connection is active

#### Scenario: Revocation fences live connections for that key
- **WHEN** a device key is marked revoked while one or more live connections are tagged with its `keyId`
- **THEN** the network host closes every such connection, regardless of which session each is attached to

### Requirement: Running-turn-aware detached lifetime
On detected detach, the network host SHALL start a grace timer. **Every detected disconnect SHALL arm the grace timer** — this includes an orderly connection-control `detach`, a heartbeat-detected dead connection, **and a drain/send failure on the forwarding path that clears the handler's current connection**: no code path may leave a handler with a cleared connection but an un-armed grace timer, since such a handler is never reaped. A **created-but-never-attached** handler (registered after a session `create` but never attached by a client) SHALL likewise be reapable after the idle TTL, with its reap clock cleared on the first successful `attach`. An idle detached handler (no turn in flight) SHALL be reaped after a configurable idle TTL. A detached handler with a turn in flight SHALL be retained until the turn completes, after which the idle TTL applies, bounded by a configurable absolute maximum. The number of concurrently live handlers SHALL be bounded by a configurable cap. On reap the network host SHALL terminate the handler's `dmoncore` process and release its per-session event-replay buffer.

#### Scenario: Idle detached handler reaped
- **WHEN** a handler has been detached with no turn in flight for longer than the idle TTL
- **THEN** the network host terminates its `dmoncore` process and removes it from the registry

#### Scenario: In-flight turn survives detach
- **WHEN** a connection drops while a turn is running
- **THEN** the handler is retained until the turn completes (bounded by the absolute maximum) rather than reaped at the idle TTL

#### Scenario: Drain-failure disconnect is reapable
- **WHEN** the forwarding path's send/pump to a connection fails and the handler clears that connection, and the client does not reattach
- **THEN** the handler's grace timer is armed (as for an orderly detach) and the handler is reaped after the idle TTL, terminating its `dmoncore` process and releasing its buffer, rather than leaking indefinitely

#### Scenario: Created-but-never-attached handler reaped
- **WHEN** a session is created and its handler registered, but no client ever attaches to it within the idle TTL
- **THEN** the handler is reaped — its `dmoncore` process terminated and it is removed from the registry — rather than leaking indefinitely

#### Scenario: Attach before the grace TTL keeps the session alive
- **WHEN** a client attaches to a created-but-never-attached handler (or reattaches to a drain-failure-detached handler) before the idle TTL elapses
- **THEN** the reap clock is cleared and the handler is retained, serving the connection normally

### Requirement: Heartbeat liveness
The network host SHALL operate application-level `ping`/`pong` heartbeats on a configurable interval to keep the connection alive across carrier NAT idle-timeouts and to detect dead connections. The detached-lifetime grace timer SHALL begin on *detected* disconnect.

#### Scenario: Dead connection detected and detached
- **WHEN** heartbeats are not answered within the configured interval
- **THEN** the network host treats the connection as detached and starts the detached-lifetime grace timer

### Requirement: Permission prompts park while detached
While a session is detached, a turn that reaches an ADR-006 permission gate SHALL park: the network host SHALL neither auto-approve nor auto-deny the request. On reattach the parked request SHALL be delivered to the client; if the handler is reaped under its TTL before reattach, the parked request SHALL be abandoned with the turn.

#### Scenario: Prompt withheld while detached
- **WHEN** a turn reaches a permission gate and no connection is attached
- **THEN** the request is held unresolved and no approval or denial is applied

#### Scenario: Parked prompt delivered on reattach
- **WHEN** a client reattaches while a permission request is parked
- **THEN** the network host delivers that request to the client for resolution

### Requirement: Tailscale-fronted authentication
The network host SHALL bind to loopback by default and SHALL NOT listen on a public network interface. Transport encryption, device identity, and per-device revocation SHALL be delegated to Tailscale (`tailscale serve` fronts the loopback network host with a Let's Encrypt certificate for the MagicDNS name). The network host MAY additionally require a device key presented as `Authorization: Bearer <token>` on the WebSocket upgrade, validated against a **per-device key set** of credential entries `{keyId, name, secretHash, createdAt, revokedAt?}`. The presented token SHALL be matched against the **active** (non-revoked) entries by hashing it (SHA-256) and comparing in constant time (`FixedTimeEquals`). On a match the upgrade SHALL be authorized and the resulting connection SHALL be tagged with the matched `keyId`. A missing token, a token matching no active entry, or a token matching only a revoked entry SHALL reject the upgrade with HTTP 401 before any socket is opened. When the active key set is **empty** the check SHALL be disabled and every upgrade authorized, identical to an unconfigured key today. Device keys are per-device, never per-session; a device key authenticates the *connection*, and `sessionId` SHALL remain a routing key and never an authentication credential.

#### Scenario: Loopback bind by default
- **WHEN** the network host starts with no explicit bind address
- **THEN** it listens only on loopback

#### Scenario: Empty key set disables the check
- **WHEN** the active device-key set is empty and a WebSocket upgrade arrives
- **THEN** the upgrade is authorized regardless of any `Authorization` header

#### Scenario: Active device key authorizes and tags the connection
- **WHEN** the active set is non-empty and an upgrade arrives bearing a token whose SHA-256 matches an active (non-revoked) entry
- **THEN** the upgrade is authorized and the connection is tagged with that entry's `keyId`

#### Scenario: Unknown or revoked key rejected on upgrade
- **WHEN** the active set is non-empty and an upgrade arrives with no token, a token matching no active entry, or a token matching only a revoked entry
- **THEN** the upgrade is rejected with HTTP 401 and no WebSocket is established

### Requirement: File-backed device-key store with hot reload
The network host SHALL source its active device-key set from a local, root-owned, mode-`0600` `devices.json` under its state directory, which it treats as read-only (the operator app is the sole writer). The network host SHALL watch that file and reload the active set when it changes, so pairing and revocation take effect without a network host restart. When the file is malformed or transiently unreadable, the network host SHALL retain the last known-good set in force and log the failure (fail closed to the previously-known-good credentials, never fail open to "disabled"). The network host SHALL record per-`keyId` last-seen activity to a separate network-host-owned `lastseen.json` for operator attribution, written on attach and throttled to bound write amplification; the network host is the sole writer of that file.

#### Scenario: Pairing takes effect without restart
- **WHEN** a new credential entry is appended to `devices.json` while the network host is running
- **THEN** the network host reloads and a subsequent upgrade bearing that entry's token is authorized, with no restart

#### Scenario: Revocation takes effect without restart
- **WHEN** an entry in `devices.json` gains a `revokedAt` value while the network host is running
- **THEN** the network host reloads, subsequent upgrades bearing that token are rejected with HTTP 401, and any live connection tagged with that `keyId` is fenced

#### Scenario: Malformed store retains last known-good set
- **WHEN** `devices.json` becomes malformed or unreadable after a valid load
- **THEN** the network host keeps the previously-loaded active set in force and logs the failure rather than disabling the check

#### Scenario: Last-seen recorded on attach
- **WHEN** a connection tagged with a `keyId` attaches
- **THEN** the network host records a last-seen timestamp for that `keyId` in `lastseen.json`, subject to the configured throttle

### Requirement: Profile-selecting session creation
Session creation SHALL allocate a `sessionId`, select an **agent** — the name of a `.cs` composition root resolved under the network host's configured workspace root (ADR-022 D14), never a client-supplied path — provision the per-session storage directory (ADR-004), and spawn the `SessionHandler`. A requested agent that does not resolve to a `.cs` composition root under the configured workspace root SHALL fail session creation with an actionable error.

#### Scenario: Session created under an agent
- **WHEN** a client creates a session specifying an agent
- **THEN** the network host spawns a handler whose core runs that agent's `.cs` composition root and returns the new `sessionId`

#### Scenario: Unknown agent fails creation
- **WHEN** session creation requests an agent that does not resolve to a `.cs` composition root under the configured workspace root
- **THEN** creation fails with an actionable error and no handler is spawned

### Requirement: Single server instance for V1
The session registry SHALL be in-process and `sessionId → SessionHandler` affinity SHALL be local to the single network host instance. The network host SHALL NOT attempt to migrate a live session to another instance or share the registry across instances in V1.

#### Scenario: Reattach requires the owning instance
- **WHEN** a client reattaches with a `sessionId`
- **THEN** the attach is served by the same network host instance that holds that session's handler

### Requirement: Network session-create control frame
The network host WebSocket surface SHALL accept a `create` control frame carrying an optional `agent`, in addition to the existing `attach` frame. On a successful create the network host SHALL spawn a `dmoncore` process, drive it to create a session under the requested agent, register the resulting `SessionHandler`, and reply with a typed `created` frame carrying the new `sessionId` (ADR-015 — a typed, correlated result, not a generic response envelope). The `agent` value SHALL name a `.cs` composition root resolved under the network host's configured workspace root and SHALL NOT be a client-supplied path. The client SHALL then `attach` to that `sessionId` through the existing attach flow. A create frame SHALL be valid as a first frame on a connection, alongside `attach`.

#### Scenario: Create spawns an agent-bound session
- **WHEN** a client sends `create {agent: "researcher"}` and `researcher` resolves to a `.cs` composition root under the configured workspace root
- **THEN** the network host spawns a core, creates a session whose record stores `agent` = `"researcher"`, registers the handler, and replies `created {sessionId}`

#### Scenario: Create without an agent
- **WHEN** a client sends `create {}` with no agent
- **THEN** the network host spawns a core, creates a session with no stored agent (resolving to the default `.cs` composition root), registers the handler, and replies `created {sessionId}`

#### Scenario: Client attaches to the created session
- **WHEN** the client has received `created {sessionId}`
- **THEN** sending `attach {sessionId, lastSeq: 0}` attaches to the spawned handler through the existing attach flow

### Requirement: Pre-spawn profile validation rejects unknown profiles
The network host SHALL validate the requested **agent** against the agents resolvable under the configured workspace root **before** spawning any core process. A requested agent that does not resolve to a `.cs` composition root SHALL be rejected with a typed, actionable error naming the unknown agent, and SHALL NOT spawn a core, SHALL NOT register a handler, and SHALL NOT consume a slot against the concurrent-handler cap. The agent name SHALL be resolved under the configured workspace root only; a client-supplied path SHALL NOT be accepted. Validation at the network host is an early-rejection convenience; the core's own first-turn resolution remains authoritative.

#### Scenario: Unknown agent rejected without spawning
- **WHEN** a client sends `create {agent: "nope"}` and `nope` does not resolve to a `.cs` composition root under the configured workspace root
- **THEN** the network host replies with an actionable error naming `nope`, spawns no core, and registers no handler

#### Scenario: No handler leaked on rejection
- **WHEN** a create is rejected for an unknown agent
- **THEN** the registry handler count is unchanged and no orphaned core process remains

### Requirement: Cap-enforced create registration
The network host SHALL register a newly created session's handler under the concurrent-handler cap (`MaxConcurrentHandlers`) using the cap-enforcing registration primitive. When the cap is already reached, create SHALL fail with a typed, actionable error and SHALL tear down any core it spawned for that create, leaving no orphaned process and no registry entry. Reattaching to an existing session SHALL NOT be subject to the cap.

#### Scenario: Create rejected at the cap
- **WHEN** the registry already holds `MaxConcurrentHandlers` handlers and a client sends a valid `create`
- **THEN** the network host replies with an actionable cap error, the spawned core (if any) is torn down, and no new handler is registered

#### Scenario: Reattach is exempt from the cap
- **WHEN** the cap is reached and a client `attach`es to an already-registered session
- **THEN** the attach succeeds, since reattach does not allocate a new handler

### Requirement: Create-handshake results are excluded from the event stream

During `create`, the network host SHALL fully consume the `session.create` and `session.load`
handshake result events (`session.createResult`, `session.loadResult`) before the session's
`SessionHandler` begins assigning per-session sequence numbers (ADR-014). As a consequence,
those handshake result events SHALL NOT be assigned a `seq`, SHALL NOT enter the handler's
`seq`-indexed buffer, and SHALL NOT be replayed to any client on attach. The first event the
handler sequences for a session SHALL be a post-handshake event, never `session.createResult`
or `session.loadResult`.

#### Scenario: Handshake results never reach the seq stream

- **WHEN** the network host handles a `create` control frame, drives the `session.create` →
  path-less `session.load` handshake to success, registers the resulting `SessionHandler`, and
  then forwards subsequent core events to an attached client
- **THEN** no event carrying `seq` is a `session.createResult` or `session.loadResult`; the
  lowest `seq` assigned for the session corresponds to the first event emitted after the
  handshake completed

#### Scenario: Create success reports the new session id

- **WHEN** the network host handles a `create` control frame and the core completes the handshake
- **THEN** the network host replies with a typed `created` frame carrying the `sessionId` returned by
  the core, and the session's handler is registered and attachable under that `sessionId`

### Requirement: Deployment guide documents the per-device key model

The operator deployment guide (`docs/deploying-the-network.md`) SHALL accurately document the shipped per-device key authentication model (ADR-018) and SHALL NOT reference a single pre-shared `SharedKey` / `NETWORK__SharedKey`, which the host does not implement. This is a documentation-accuracy requirement; it adds no system behaviour and does not modify the "Tailscale-fronted authentication" or "File-backed device-key store with hot reload" requirements, which remain the source of truth for the behaviour itself.

The guide SHALL describe: the `devices.json` store and its default location (`~/.dmon/network/devices.json`, overridable via `Network:DeviceKeyStoreDirectory`); the `{ schemaVersion, devices: [{ keyId, name, secretHash, createdAt, revokedAt? }] }` schema with `secretHash` being the SHA-256 hex of the device token (never the token in clear); that an empty or absent store disables authentication (open over the tailnet, operator cannot lock themselves out) and the first active entry enables enforcement; how to enrol a device (the concrete manual path: hand-edit `devices.json`, computing `secretHash` from the token) and the dmonium pairing surface only to the extent it ships; revocation via `revokedAt` and that it fences live connections for that `keyId`; and hot-reload (the host watches `devices.json`; changes take effect without restart; a malformed file fails closed to last-good).

#### Scenario: Guide describes per-device enrolment, not a shared key

- **WHEN** an operator follows `docs/deploying-the-network.md` to enable authentication
- **THEN** the guide instructs them to add a device entry to `devices.json` (with a SHA-256 `secretHash` of the device token) rather than to set a `Network:SharedKey`, and the device authenticates by presenting that token as `Authorization: Bearer <token>`

#### Scenario: No stale shared-key references remain

- **WHEN** the deployment guide is reviewed for auth content
- **THEN** it contains no `Network:SharedKey` / `NETWORK__SharedKey` configuration references, no "single pre-shared key" prose, and no reference-table row for `Network:SharedKey`; and the configuration reference table lists `Network:DeviceKeyStoreDirectory` and `Network:LastSeenThrottleSeconds`

#### Scenario: Documented facts match the shipped host

- **WHEN** the documented `devices.json` schema, default store path, hash algorithm, revocation/fencing behaviour, hot-reload semantics, and config keys are checked against `frontends/Dmon.Network/` and ADR-018
- **THEN** every documented fact matches the shipped code (no field-name, default-path, algorithm, or config-key drift)

