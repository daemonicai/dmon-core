## 1. Device-key store model and reader

- [x] 1.1 Add device-credential record types (`keyId`, `name`, `secretHash`, `createdAt`, `revokedAt?`) and a `schemaVersion: 1` envelope for `devices.json`, using dmon-owned types only.
- [x] 1.2 Implement a `devices.json` reader that parses the envelope into an immutable active-set snapshot (non-revoked entries only), ignoring unknown fields (room for future `expiresAt` per design D7).
- [x] 1.3 Define the active-set snapshot abstraction (immutable, swappable behind a single reference) the auth path and reload path share.
- [x] 1.4 Unit tests: valid file → active set excludes revoked; empty/absent file → empty (disabled) set; malformed file → parse error surfaced (consumed by the watcher in group 4).

## 2. DeviceKeyAuthenticator

- [x] 2.1 Generalize `SharedKeyAuthenticator` → `DeviceKeyAuthenticator`: keep `Bearer` header parsing (case-insensitive, RFC 7235); SHA-256 the presented token once; `FixedTimeEquals` against each active entry's `secretHash`; return the matched `keyId` (or no-match).
- [x] 2.2 Empty active set short-circuits to authorized (byte-for-byte today's `null`-disables semantics).
- [x] 2.3 Unit tests: empty set authorizes any/no header; active match authorizes and yields its `keyId`; unknown token, missing header, malformed scheme, and revoked-only match all reject (→ 401 at the endpoint).

## 3. Options and config wiring

- [x] 3.1 Remove `GatewayOptions.SharedKey`; add `DeviceKeyStoreDirectory` (state-dir-relative default) and a last-seen throttle setting (default 60 s).
- [x] 3.2 Update `appsettings.json` (and the Release copy) to drop `SharedKey` and document the store directory.
- [x] 3.3 Wire `DeviceKeyAuthenticator` into the upgrade path in `GatewayConnectionEndpoint`/`Program.cs`, rejecting with HTTP 401 before any socket opens; remove the old scalar path.
- [x] 3.4 Update existing auth tests (`AuthAndBindTests`) to the device-key model (file-backed set in place of the scalar key).

## 4. Hot reload with fail-closed-to-last-good

- [x] 4.1 Add a debounced `FileSystemWatcher` on `devices.json` that rebuilds the snapshot on change and swaps the shared reference.
- [x] 4.2 On parse/IO failure after a prior good load, retain the last-good snapshot and log at warning — never collapse to the empty/disabled set. Absent-at-startup means empty (disabled).
- [x] 4.3 Tests: pairing append takes effect with no restart; malformed-after-good keeps enforcing the last-good set; absent-at-startup disables the check.

## 5. Connection tagging and by-keyId index

- [x] 5.1 Tag each authorized `IGatewayConnection` with its matched `keyId` at attach time.
- [x] 5.2 Maintain a `keyId → live connections` index alongside `SessionRegistry`/`SessionHandler`, updated in the same attach/detach path that manages generation (remove on detach and on eviction).
- [x] 5.3 Tests: index gains a connection on attach, drops it on normal detach and on generation-eviction, and spans multiple sessions for one `keyId`.

## 6. Revocation fencing

- [x] 6.1 On reload, diff the active set to find newly-revoked `keyId`s and, for each, enumerate the by-`keyId` index and `Abort()` every live connection (reusing the existing evict-and-close primitive), across all sessions.
- [x] 6.2 Tests: revoking a key rejects subsequent upgrades (401) and fences all live connections tagged with that `keyId`; an unrelated key's connections are unaffected.

## 7. Last-seen telemetry

- [ ] 7.1 Implement the gateway-owned `lastseen.json` writer (`schemaVersion: 1`, per-`keyId` ISO-8601 timestamp), written on attach, throttled per `keyId` per the configured interval, best-effort (failure logged, never affects auth/connection).
- [ ] 7.2 Tests: attach records a last-seen; repeated attaches within the throttle window coalesce; a write failure does not surface to the connection.

## 8. End-to-end and spec validation

- [ ] 8.1 End-to-end gateway test: empty set → open; pair (file append) → only the matching token attaches; revoke (file write) → 401 + live connection fenced — no restart across the sequence.
- [ ] 8.2 `make build` clean (TreatWarningsAsErrors), `make test` green, `openspec validate gateway-per-device-keys --strict` passes.
