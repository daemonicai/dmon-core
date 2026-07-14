## MODIFIED Requirements

### Requirement: Tailscale-fronted authentication
The network host SHALL bind to loopback by default and SHALL NOT listen on a public network interface. Transport encryption, device identity, and per-device revocation SHALL be delegated to Tailscale (`tailscale serve` fronts the loopback network host with a Let's Encrypt certificate for the MagicDNS name). The network host MAY additionally require a device key presented as `Authorization: Bearer <token>` on the WebSocket upgrade, validated against a **per-device key set** of credential entries `{keyId, name, secretHash, createdAt, revokedAt?}`. The presented token SHALL be matched against the **active** (non-revoked) entries by hashing it (SHA-256) and comparing in constant time (`FixedTimeEquals`). On a match the upgrade SHALL be authorized and the resulting connection SHALL be tagged with the matched `keyId`. A missing token, a token matching no active entry, or a token matching only a revoked entry SHALL reject the upgrade with HTTP 401 before any socket is opened. When the active key set is **empty** and the effective bind is **loopback**, the check SHALL be disabled and every upgrade authorized, identical to an unconfigured key today; when the active key set is **empty** and the effective bind is **non-loopback**, the network host SHALL instead fail closed and reject every upgrade with HTTP 401 (see "Empty key set fails closed on a non-loopback bind"). Device keys are per-device, never per-session; a device key authenticates the *connection*, and `sessionId` SHALL remain a routing key and never an authentication credential.

#### Scenario: Loopback bind by default
- **WHEN** the network host starts with no explicit bind address
- **THEN** it listens only on loopback

#### Scenario: Empty key set disables the check
- **WHEN** the effective bind is loopback, the active device-key set is empty, and a WebSocket upgrade arrives
- **THEN** the upgrade is authorized regardless of any `Authorization` header

#### Scenario: Active device key authorizes and tags the connection
- **WHEN** the active set is non-empty and an upgrade arrives bearing a token whose SHA-256 matches an active (non-revoked) entry
- **THEN** the upgrade is authorized and the connection is tagged with that entry's `keyId`

#### Scenario: Unknown or revoked key rejected on upgrade
- **WHEN** the active set is non-empty and an upgrade arrives with no token, a token matching no active entry, or a token matching only a revoked entry
- **THEN** the upgrade is rejected with HTTP 401 and no WebSocket is established

## ADDED Requirements

### Requirement: Empty key set fails closed on a non-loopback bind
The network host SHALL treat an empty or absent active device-key set as authentication **disabled only when the effective bind is loopback**. When the effective bind is **non-loopback** — a specific non-loopback address permitted via the non-loopback-bind opt-in; wildcard / all-interfaces binds are rejected at startup and never reach this state — and the active device-key set is empty or absent, the network host SHALL reject every `/ws` upgrade with HTTP 401 before any socket is opened, rather than authorizing via the empty-set short-circuit. A **non-empty** active key set SHALL continue to enforce per-device Bearer authentication on any bind, unchanged. This preserves the local-dev "the operator cannot lock themselves out on loopback" property while never failing open on an exposed interface, consistent with the fail-closed posture already required for a malformed store.

#### Scenario: Empty set on a non-loopback bind rejects the upgrade
- **WHEN** the effective bind is non-loopback (a specific non-loopback address with the opt-in enabled), the active device-key set is empty, and a WebSocket upgrade arrives
- **THEN** the upgrade is rejected with HTTP 401 before any socket is opened, regardless of any `Authorization` header

#### Scenario: Empty set on a loopback bind still authorizes
- **WHEN** the effective bind is loopback and the active device-key set is empty
- **THEN** the upgrade is authorized via the empty-set short-circuit, exactly as before this change

#### Scenario: Non-empty set is enforced on a non-loopback bind
- **WHEN** the effective bind is non-loopback and the active device-key set is non-empty
- **THEN** per-device Bearer authentication applies exactly as on a loopback bind: a matching active token is authorized and tagged, and a missing or unmatched token is rejected with HTTP 401

#### Scenario: Absent device store on a non-loopback bind rejects
- **WHEN** the effective bind is non-loopback and no `devices.json` exists (the active set is absent/empty)
- **THEN** every `/ws` upgrade is rejected with HTTP 401 and the network host records that authentication is fail-closed until a device is paired, rather than logging "auth disabled"

### Requirement: Browser-Origin allowlist on the WebSocket upgrade
The network host SHALL evaluate the `Origin` request header on every `/ws` upgrade **before** accepting the WebSocket, as defence-in-depth atop the device-key authentication: both checks SHALL apply, and passing one SHALL NOT waive the other. A request that carries **no** `Origin` header — native clients such as the iOS and desktop apps, which do not set `Origin` — SHALL be allowed to proceed to the device-key check. A request that carries an `Origin` header SHALL be rejected with HTTP 403 before any socket is opened unless the origin exactly matches a configured allowlist. The allowlist SHALL be sourced from the network host configuration (`Network:AllowedOrigins`) and SHALL default to empty, so by default every `Origin`-bearing (browser) upgrade is rejected. Matching SHALL be exact string comparison against the configured origins; the `Origin` check SHALL never substitute for, or relax, the device-key check.

#### Scenario: Origin-less native client is allowed through
- **WHEN** a `/ws` upgrade arrives with no `Origin` header
- **THEN** the network host does not reject on origin grounds and proceeds to the device-key authentication check

#### Scenario: Unlisted browser origin is rejected by default
- **WHEN** a `/ws` upgrade arrives carrying an `Origin` header and the configured allowlist is empty (the default) or does not contain that exact origin
- **THEN** the upgrade is rejected with HTTP 403 before any socket is opened

#### Scenario: Allowlisted origin proceeds to authentication
- **WHEN** a `/ws` upgrade arrives carrying an `Origin` header whose value exactly matches an entry in the configured allowlist
- **THEN** the network host does not reject on origin grounds and proceeds to the device-key authentication check

#### Scenario: Origin allowlist does not bypass device-key auth
- **WHEN** a `/ws` upgrade carries an allowlisted `Origin` but fails the device-key check (a non-empty active set with a missing or unmatched token)
- **THEN** the upgrade is still rejected with HTTP 401, because the two checks are independent and both must pass
