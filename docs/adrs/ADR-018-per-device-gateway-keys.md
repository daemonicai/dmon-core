# ADR-018: Per-Device Gateway Keys — A Revocable Key Set Replaces the Single Shared Key

**Date:** 2026-06-13
**Status:** Accepted
**Amends:** ADR-012 (Decisions 10 & 12; extends Decision 6's fencing trigger)

## Context

ADR-012 made Tailscale the authentication and encryption boundary for the remote
gateway and added, as defense-in-depth, an **optional single pre-shared key**: Decision
12's final bullet specifies one key, generated at first run, stored once in the iOS
Keychain, sent as `Authorization: Bearer <key>` on the WebSocket upgrade, with a mismatch
rejecting the upgrade with HTTP 401. That decision is implemented today by
`Dmon.Gateway`'s `SharedKeyAuthenticator.IsAuthorized(authorizationHeader, sharedKey)` —
a constant-time (`CryptographicOperations.FixedTimeEquals`) comparison of the presented
bearer token against the scalar `GatewayOptions.SharedKey` (`string?`). When that key is
`null`/empty the check is disabled and every upgrade is authorized; when set, the upgrade
must carry a matching bearer token.

A new consumer changes the requirement. `dmonium` — the macOS menu-bar app that operates
the gateway on the host Mac (see `dmonium/docs/requirements.md`) — pairs client devices by
showing a QR code that carries the gateway's MagicDNS `wss://` URL plus a key, which the
device stores in its Keychain. Its requirements call for **per-device keys with
independent revocation**: losing one phone must not force re-pairing every other device,
and the operator wants per-device attribution (a name and a last-seen time) in the
pairing UI.

A single shared key cannot satisfy this. Revoking it (rotating the one secret) invalidates
**every** device at once; there is no per-device identity to attribute activity to. The
scalar must become a **set**.

This is a narrow change at the gateway's auth edge. It does **not** touch the transport
(ADR-012), the wire protocol (ADR-003), session storage (ADR-004), provider auth
(ADR-005), or the permission model (ADR-006). It also does **not** weaken ADR-012's core
posture: Tailscale remains the boundary, the key set remains *defense-in-depth*, and the
deployment stays **permanently single-tenant** — multiple keys are multiple credentials
for the **one** principal, not multiple tenants.

## Decision

1. **The scalar shared key becomes a per-device key set.** Each entry is a **device
   credential**: `{ keyId, name, secretHash, createdAt, revokedAt? }`. On a WebSocket
   upgrade the presented bearer token is matched against the **active** (non-revoked)
   entries; a match authorizes the upgrade **and tags the connection with that `keyId`**
   (the device identity); no match — or a match against a revoked entry — rejects the
   upgrade with **HTTP 401**, exactly as today. `SharedKeyAuthenticator` is generalized
   to a `DeviceKeyAuthenticator` over the set; `GatewayOptions.SharedKey` is superseded by
   a reference to the device-key store (Decision 4).

2. **The empty set means "disabled," preserving ADR-012's *optional* key.** When the set
   is empty the check is disabled and every upgrade is authorized over the tailnet —
   byte-for-byte the meaning of today's `null` scalar, so ADR-012's "optional shared key /
   Tailscale-is-the-boundary" property is retained and an operator cannot lock themselves
   out. Enforcement begins automatically once the first device is paired (set size ≥ 1).

3. **Keys are stored as hashes, never in the clear.** A device key is a high-entropy
   random token (≥ 256 bits). The store holds **`secretHash` = SHA-256 of the token**, and
   matching hashes the presented token and compares in constant time
   (`FixedTimeEquals`), as the current code already does for the scalar. A slow password
   KDF (argon2/bcrypt) is deliberately **not** used: these are high-entropy machine-minted
   tokens, not user-chosen passwords, so a fast digest plus constant-time compare is
   sufficient and simpler. The raw token exists only at mint time (encoded into the QR)
   and on the device; the gateway never persists it.

4. **The key set is a file-backed store the gateway reads and the operator app writes —
   with one writer per file.** Two local, root-owned, mode-`0600` files under the gateway's
   state directory:
   - **`devices.json`** — **owned by `dmonium`.** The credential rows of Decision 1
     (`keyId`, `name`, `secretHash`, `createdAt`, `revokedAt?`). `dmonium` appends a row
     when pairing (mint-on-pair) and sets `revokedAt` on revoke. The gateway treats it as
     **read-only**.
   - **`lastseen.json`** — **owned by the gateway.** A per-`keyId` last-seen timestamp,
     written (throttled — on attach, not on every heartbeat) so `dmonium` can display
     device activity. `dmonium` treats it as read-only.

   Splitting identity (operator-written) from telemetry (gateway-written) keeps **exactly
   one writer per file**, eliminating write contention without a lock protocol. Both
   processes run on the same Mac, so a shared filesystem is the natural channel; a loopback
   gateway admin API is rejected (Alternatives) as it adds an endpoint and its own auth
   bootstrap for no benefit on a single box. This file pair is also the concrete answer to
   the operator app's unattended-secret-delivery question (`dmonium` DEP-3): the operator
   writes local root-owned files, the boot-time daemon reads them — no login-Keychain
   dependency.

5. **The gateway hot-reloads the active set on change.** The gateway **watches**
   `devices.json` and reloads the active key set when it changes, so a pairing or a
   revocation takes effect **without a gateway restart**. A malformed or transiently
   unreadable file leaves the last good set in force and is logged (fail-closed to the
   previously-known-good credentials, not fail-open to "disabled").

6. **Revocation also fences any live connection authenticated by that key.** Marking an
   entry `revokedAt` MUST additionally **close any currently-attached connection tagged
   with that `keyId`**, reusing ADR-012 Decision 6's generation/evict-and-close machinery —
   now triggered by *revocation*, not only by a competing reattach. Otherwise a
   revoked-but-still-connected device would retain a live socket until its next upgrade.
   Revocation is therefore: (a) `dmonium` writes `revokedAt`; (b) the gateway, on reload,
   drops the active entry and (c) fences/closes its live connection if one exists.

7. **`sessionId` remains a routing key, not an auth credential; keys are per-device, not
   per-session.** Unchanged from ADR-012 Decision 10. A device key authenticates the
   *connection*, never a session; any authorized device may attach to or resume any
   session (the single-principal, cross-device resume of ADR-012). This ADR introduces no
   per-device session partitioning and no ownership matrix — to do so would contradict the
   single-tenant collapse of ownership that ADR-012 Decision 12 relies on.

## Consequences

- **Per-device revocation and attribution become possible** with the blast radius confined
  to the gateway's auth edge: `SharedKeyAuthenticator` → `DeviceKeyAuthenticator`,
  `GatewayOptions.SharedKey` → a store reference + the two state files. The transport, the
  wire contract, and the core are untouched.
- **ADR-012's posture is preserved.** Tailscale is still the boundary; the key set is still
  defense-in-depth; single tenancy is intact (many credentials, one principal). The
  empty-set-disabled rule keeps the key *optional*.
- **Operability without restarts.** Pairing and revocation are file writes the gateway
  picks up live (Decision 5); revocation promptly severs the device (Decision 6).
- **A leaked store leaks no usable credentials.** Hashes at rest (Decision 3) mean
  `devices.json` exposure does not yield working bearer tokens; combined with per-device
  revocation, the blast radius of any single compromised device is one row.
- **Two new state files and a file watcher** join the gateway; `appsettings.json` gains the
  store directory path and drops the inline `SharedKey`.
- **Secret-at-rest protection still depends on the host.** `devices.json` holds only
  hashes, but `lastseen.json`, session logs, and the workspace remain in the clear unless
  the host disk is encrypted — a deployment concern (FileVault) owned by the operator, out
  of scope here.

## Alternatives

- **Keep the single shared key; rotate-all on any device loss (ADR-012 as written).**
  Rejected: re-pairing every device because one was lost is exactly the technical debt the
  operator chose to avoid, and it offers no per-device attribution.
- **A loopback gateway admin API for pairing/revocation instead of files.** Rejected for
  V1: it adds an HTTP surface and its own authentication bootstrap (what guards the admin
  API?) for no benefit when the operator app and the gateway share a disk. The file pair is
  simpler and single-writer-clean.
- **One combined `devices.json` written by both processes.** Rejected: two writers on one
  file needs a lock or merge protocol; the identity/telemetry split (Decision 4) gives
  single-writer semantics for free.
- **A slow password KDF (argon2/bcrypt) for `secretHash`.** Rejected: device keys are
  high-entropy machine-minted tokens, not user passwords; SHA-256 + constant-time compare
  is sufficient and avoids per-upgrade KDF cost.
- **Per-device session authorization (each device sees only its own sessions).** Rejected:
  it contradicts ADR-012's single-tenant, cross-device resume and introduces an ownership
  matrix the deployment deliberately does not have.

## Open Questions

- **A. Store schema versioning and exact location.** The precise path under the gateway
  state directory and a `schemaVersion` field for `devices.json` / `lastseen.json` need
  pinning when the change is implemented.
- **B. Key format and prefix.** Token length, encoding (base64url), and an optional
  human-recognizable prefix (e.g. `dmon_…`) for accidental-paste detection are
  unspecified here.
- **C. Last-seen write granularity.** "On attach, throttled" (Decision 4) needs a concrete
  throttle (e.g. at most once per N seconds per `keyId`) to bound write amplification.
- **D. Optional key expiry.** Whether a device credential may carry an `expiresAt` (auto-
  revoke) in addition to manual revocation is deferred; the schema should leave room for
  it.

## Relationship to other ADRs

- **ADR-012** — *Amends Decisions 10 & 12 and extends Decision 6.* Decision 12's single
  optional shared key generalizes to a per-device, revocable key set; Decision 10's
  "`sessionId` is not an auth credential" is reaffirmed (Decision 7 here); Decision 6's
  fencing is now also triggered by revocation (Decision 6 here). The transport, session
  decoupling, control sub-protocol, and single-tenant auth model are otherwise unchanged.
- **ADR-003 / ADR-004 / ADR-005 / ADR-006** — untouched. This change lives entirely at the
  gateway's HTTP-upgrade auth edge; the wire contract, session storage, provider auth, and
  permission model are unaffected.
- **ADR-017** — orthogonal. How the iOS client reaches the tailnet (embedded node vs system
  app) is independent of how its bearer key is minted, stored, and revoked.
- **`dmonium/docs/requirements.md`** — resolves that document's **DEP-1** (per-device keys)
  and supplies the file-based mechanism underpinning **DEP-3** (unattended secret
  delivery). Realizes requirements **FR-PAIR-1/2/3**.
</content>
