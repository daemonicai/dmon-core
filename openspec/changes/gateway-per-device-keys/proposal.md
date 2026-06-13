## Why

The gateway's defense-in-depth credential is a single pre-shared key (`SharedKeyAuthenticator` over `GatewayOptions.SharedKey`). Revoking it invalidates **every** paired device at once and carries no per-device identity, so the operator app (`dmonium`) cannot offer per-device revocation or attribution. ADR-018 (Accepted) replaces the scalar key with a revocable per-device key set at the gateway's auth edge.

## What Changes

- **BREAKING** Retire the scalar `GatewayOptions.SharedKey`. The gateway reads its credentials from a file-backed device-key store instead. (No prod deployments — clean break, no migration of an existing inline key.)
- Generalize `SharedKeyAuthenticator` → `DeviceKeyAuthenticator`: match the presented `Authorization: Bearer <token>` against the **active** (non-revoked) device-credential entries `{keyId, name, secretHash, createdAt, revokedAt?}` using SHA-256 + `FixedTimeEquals`; on a match, authorize the upgrade **and tag the connection with that `keyId`**; on no-match or a match against a revoked entry, reject with HTTP 401 before any socket opens.
- **Empty set = disabled**, byte-for-byte the meaning of today's `null` scalar (preserves ADR-012's optional-key / Tailscale-is-the-boundary posture; an operator cannot lock themselves out). Enforcement begins automatically once the first device is paired.
- Introduce a two-file store under the gateway state directory, **one writer per file**: `devices.json` (operator/`dmonium`-owned credential rows; gateway treats as read-only) and `lastseen.json` (gateway-owned per-`keyId` last-seen timestamps, written throttled on attach; `dmonium` treats as read-only).
- The gateway **watches `devices.json`** and hot-reloads the active set on change (pairing/revocation take effect with no restart). A malformed/unreadable file **fails closed** to the last-known-good set, never fail-open to "disabled".
- **Revocation fences live connections.** Marking an entry `revokedAt` MUST close any currently-attached connection tagged with that `keyId`, reusing the `SessionHandler` evict-and-close primitive — with a **new cross-session by-`keyId` lookup**, since connections are not currently tagged or indexed by `keyId`.
- `sessionId` stays a routing key, not an auth credential; keys are per-device, not per-session (ADR-012 Decision 10 reaffirmed). No per-device session partitioning.

## Capabilities

### New Capabilities
<!-- None — the change lives entirely within the existing gateway capability's auth edge. -->

### Modified Capabilities
- `remote-session-gateway`: the *Tailscale-fronted authentication* requirement changes from a single optional shared key to a revocable per-device key set with connection-tagging; the *stale-connection fencing* requirement gains revocation as a second fencing trigger.

## Impact

- **Code:** `Dmon.Gateway` — `SharedKeyAuthenticator` → `DeviceKeyAuthenticator`; `GatewayOptions` (drop `SharedKey`, add store directory path); `GatewayConnectionEndpoint` (tag connection with `keyId`); `SessionRegistry`/`SessionHandler` (cross-session by-`keyId` lookup + revocation-triggered fence); new device-key store reader, `devices.json` file watcher, and throttled `lastseen.json` writer.
- **Config:** `appsettings.json` drops the inline `SharedKey`, gains the store directory path.
- **ADRs:** Realizes ADR-018 (amends ADR-012 Decisions 6, 10 & 12). Untouched: ADR-003 (wire protocol), ADR-004 (session storage), ADR-005 (provider auth), ADR-006 (permission model).
- **Cross-process contract:** the `devices.json` / `lastseen.json` file pair is the boundary with the `dmonium` operator app (resolves its DEP-1/DEP-3, FR-PAIR-1/2/3); `dmonium` itself is out of scope here.
- **Out of scope:** session-storage, transport, wire contract, and the core are unchanged.
