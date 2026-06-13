## Context

ADR-018 (Accepted) replaces the gateway's single optional pre-shared key with a revocable per-device key set, living entirely at the WebSocket-upgrade auth edge. Today `SharedKeyAuthenticator.IsAuthorized(authorizationHeader, sharedKey)` does a constant-time compare of the bearer token against the scalar `GatewayOptions.SharedKey`; `null`/empty disables the check. The fencing machinery in `SessionHandler` already evicts-and-closes a prior connection on a competing attach (per-session `generation` token, `IGatewayConnection.Abort`). This change generalizes the scalar to a set, sources it from a file the operator app (`dmonium`) writes, hot-reloads it, and adds revocation as a second fencing trigger. ADR-018's four Open Questions (A–D) are pinned here.

## Goals / Non-Goals

**Goals:**
- A `DeviceKeyAuthenticator` over an active key set that authorizes an upgrade and tags the connection with the matched `keyId`.
- File-backed `devices.json` (operator-written, gateway-read-only) + `lastseen.json` (gateway-written), one writer per file, hot-reloaded with fail-closed-to-last-good semantics.
- Revocation that both rejects future upgrades and fences live connections for that `keyId` across all sessions.
- Preserve ADR-012's posture: empty set = disabled; Tailscale is the boundary; single principal, many credentials.

**Non-Goals:**
- The `dmonium` operator app, QR pairing, and Keychain storage — out of scope (this side only reads `devices.json` / writes `lastseen.json`).
- Per-device session ownership / partitioning — explicitly rejected by ADR-018 D7 (contradicts ADR-012 cross-device resume).
- A loopback admin API for pairing/revocation — rejected by ADR-018 in favour of the file pair.
- Any change to the wire protocol (ADR-003), session storage (ADR-004), provider auth (ADR-005), or permission model (ADR-006).
- Slow password KDFs — device keys are high-entropy machine tokens; SHA-256 + `FixedTimeEquals` is sufficient (ADR-018 D3).

## Decisions

### D1 — `DeviceKeyAuthenticator` over an active set; tag the connection
Generalize `SharedKeyAuthenticator` to `DeviceKeyAuthenticator`. It keeps the same header parsing (`Bearer` scheme, case-insensitive per RFC 7235) and constant-time compare, but iterates the active (non-revoked) entries, SHA-256-hashing the presented token once and comparing against each entry's `secretHash` with `FixedTimeEquals`. The matched entry's `keyId` is returned so the upgrade path can stamp it onto the `IGatewayConnection`. Empty set short-circuits to authorized (preserves today's `null`-disables semantics). *Alternative — keep the scalar and rotate-all on loss:* rejected by ADR-018 (no per-device revocation/attribution).

### D2 — Two files, one writer each (resolves ADR-018 Open Question A: location + schemaVersion)
Both files live under the gateway state directory, root-owned, mode `0600`:
- `devices.json` — operator-owned, gateway read-only: `{ "schemaVersion": 1, "devices": [ { keyId, name, secretHash, createdAt, revokedAt? } ] }`.
- `lastseen.json` — gateway-owned, operator read-only: `{ "schemaVersion": 1, "lastSeen": { "<keyId>": "<iso8601>" } }`.

State directory path is a new `GatewayOptions` setting (e.g. `DeviceKeyStoreDirectory`), defaulting under the existing gateway state root; `GatewayOptions.SharedKey` is removed (clean break — no prod deployments, no migration). `schemaVersion: 1` is present from day one to leave room for D-Q open question (expiry). *Alternative — one combined file written by both:* rejected (needs a lock/merge protocol; the identity/telemetry split gives single-writer for free).

### D3 — Hashing (resolves Open Question B: key format)
`secretHash` = lowercase hex SHA-256 of the raw token. The raw token is minted by `dmonium` only; the gateway never sees or persists it except as the bearer value presented on upgrade. Expected token format (documented for the contract, produced by `dmonium`): `dmon_` prefix + base64url(≥256 bits) — the prefix aids accidental-paste detection. The gateway does not enforce the prefix; it only hashes-and-compares, so format remains the operator app's concern.

### D4 — Hot reload, fail-closed-to-last-good (resolves Open Question C indirectly)
A `FileSystemWatcher` (debounced) on `devices.json` triggers a reload into an immutable snapshot swapped behind a single reference read by the auth path. Parse/IO failure keeps the prior snapshot and logs at warning; it never collapses to the empty/disabled set. On startup, an absent file means the empty set (disabled) — distinct from a *present-but-unreadable* file after a good load, which holds last-good.

### D5 — Last-seen throttle (resolves Open Question C: throttle constant)
`lastseen.json` is written on attach only (not per heartbeat), throttled to at most once per `keyId` per **60 s** (a new `GatewayOptions` value, defaulted, overridable). Writes are coalesced and best-effort: a failed last-seen write is logged and never affects auth or the connection.

### D6 — Revocation fencing via a by-`keyId` connection index
Connections carry their `keyId` (D1). A new lookup — a `keyId → set of live connections` index maintained alongside `SessionRegistry`/`SessionHandler` attach/detach — lets a reload diff (entry gained `revokedAt`) enumerate and `Abort()` every affected connection across all sessions, reusing the existing evict-and-close primitive (`IGatewayConnection.Abort`, generation guard). This is genuinely new wiring: today nothing is keyed by `keyId`. The reload path computes the set of newly-revoked `keyId`s and fences each. *Alternative — wait for next upgrade:* rejected by ADR-018 D6 (a revoked-but-connected device keeps its socket).

### D7 — Optional expiry left as schema room only (resolves Open Question D)
No `expiresAt` enforcement in this change; `schemaVersion: 1` and lenient parsing leave room to add it later without a breaking bump. An unknown future field is ignored, not rejected.

## Risks / Trade-offs

- **By-`keyId` index lifecycle bugs** (leaked entries on abnormal detach) → maintain it in the same attach/detach path that already manages generation, with the detach cleanup removing the connection from the index; cover with tests for attach, normal detach, eviction, and revocation-fence.
- **Watcher misses / duplicate events** → debounce and treat reload as idempotent (full snapshot rebuild from file, not incremental); a missed event is bounded because the next change re-triggers, and revocation correctness does not depend on event timing beyond the operator's expectation of "promptly".
- **Fail-open regression** → the one rule that must never break: unreadable-after-good ⇒ last-good, never disabled. Explicit test for malformed-file-keeps-enforcing.
- **Last-seen write amplification / contention** → on-attach + 60 s throttle + best-effort; gateway is the sole writer so no contention with `dmonium`.
- **Constant-time matching over a set** → hash the presented token once, `FixedTimeEquals` per entry; set is tiny (a household's devices), so per-upgrade cost is negligible.

## Migration Plan

No data migration: no production deployments, and `GatewayOptions.SharedKey` is removed outright (clean break per project convention). Deploy is a gateway build that reads `devices.json`/`lastseen.json`; with no file present, behaviour is identical to today's unconfigured key (open over the tailnet). Rollback is reverting the gateway build; the new files are inert to any prior version.

## Open Questions

- None blocking. ADR-018 Open Questions A–D are pinned by D2/D3/D5/D7. The exact default state-directory subpath and the `GatewayOptions` property names are settled during implementation against the existing gateway state-root convention.
