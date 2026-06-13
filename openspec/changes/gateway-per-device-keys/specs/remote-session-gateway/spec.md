## MODIFIED Requirements

### Requirement: Tailscale-fronted authentication
The gateway SHALL bind to loopback by default and SHALL NOT listen on a public network interface. Transport encryption, device identity, and per-device revocation SHALL be delegated to Tailscale (`tailscale serve` fronts the loopback gateway with a Let's Encrypt certificate for the MagicDNS name). The gateway MAY additionally require a device key presented as `Authorization: Bearer <token>` on the WebSocket upgrade, validated against a **per-device key set** of credential entries `{keyId, name, secretHash, createdAt, revokedAt?}`. The presented token SHALL be matched against the **active** (non-revoked) entries by hashing it (SHA-256) and comparing in constant time (`FixedTimeEquals`). On a match the upgrade SHALL be authorized and the resulting connection SHALL be tagged with the matched `keyId`. A missing token, a token matching no active entry, or a token matching only a revoked entry SHALL reject the upgrade with HTTP 401 before any socket is opened. When the active key set is **empty** the check SHALL be disabled and every upgrade authorized, identical to an unconfigured key today. Device keys are per-device, never per-session; a device key authenticates the *connection*, and `sessionId` SHALL remain a routing key and never an authentication credential.

#### Scenario: Loopback bind by default
- **WHEN** the gateway starts with no explicit bind address
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

### Requirement: Stale-connection fencing and single active writer
Each `attach` SHALL be issued a strictly increasing `generation` token. The gateway SHALL drop and close any connection whose generation is older than the handler's current generation. A new attach to a live handler SHALL evict (fence and close) the prior connection, so a session has a single active writer. Revocation of a device key SHALL additionally trigger this fencing: when a key transitions to revoked, the gateway SHALL close any currently-attached connection tagged with that `keyId`, across all sessions, reusing the same evict-and-close path.

#### Scenario: Older generation fenced
- **WHEN** a frame arrives on a connection whose generation is less than the handler's current generation
- **THEN** the gateway ignores the frame and closes that connection

#### Scenario: New attach evicts the prior connection
- **WHEN** a new `attach` succeeds for a session that already has an attached connection
- **THEN** the prior connection is closed and only the new connection is active

#### Scenario: Revocation fences live connections for that key
- **WHEN** a device key is marked revoked while one or more live connections are tagged with its `keyId`
- **THEN** the gateway closes every such connection, regardless of which session each is attached to

## ADDED Requirements

### Requirement: File-backed device-key store with hot reload
The gateway SHALL source its active device-key set from a local, root-owned, mode-`0600` `devices.json` under its state directory, which it treats as read-only (the operator app is the sole writer). The gateway SHALL watch that file and reload the active set when it changes, so pairing and revocation take effect without a gateway restart. When the file is malformed or transiently unreadable, the gateway SHALL retain the last known-good set in force and log the failure (fail closed to the previously-known-good credentials, never fail open to "disabled"). The gateway SHALL record per-`keyId` last-seen activity to a separate gateway-owned `lastseen.json` for operator attribution, written on attach and throttled to bound write amplification; the gateway is the sole writer of that file.

#### Scenario: Pairing takes effect without restart
- **WHEN** a new credential entry is appended to `devices.json` while the gateway is running
- **THEN** the gateway reloads and a subsequent upgrade bearing that entry's token is authorized, with no restart

#### Scenario: Revocation takes effect without restart
- **WHEN** an entry in `devices.json` gains a `revokedAt` value while the gateway is running
- **THEN** the gateway reloads, subsequent upgrades bearing that token are rejected with HTTP 401, and any live connection tagged with that `keyId` is fenced

#### Scenario: Malformed store retains last known-good set
- **WHEN** `devices.json` becomes malformed or unreadable after a valid load
- **THEN** the gateway keeps the previously-loaded active set in force and logs the failure rather than disabling the check

#### Scenario: Last-seen recorded on attach
- **WHEN** a connection tagged with a `keyId` attaches
- **THEN** the gateway records a last-seen timestamp for that `keyId` in `lastseen.json`, subject to the configured throttle
