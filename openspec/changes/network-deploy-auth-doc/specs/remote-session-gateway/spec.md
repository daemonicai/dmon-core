## ADDED Requirements

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
