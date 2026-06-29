## Context

`docs/deploying-the-network.md` is the operator deployment guide for the `Dmon.Network` WebSocket host. Its auth material describes a single pre-shared bearer key (`Network:SharedKey`), which was superseded by ADR-018's per-device revocable key set and never updated. The shipped code is authoritative:

- **`NetworkOptions`** has no `SharedKey`; it has `DeviceKeyStoreDirectory` (default `~/.dmon/network/`).
- **`DeviceKeyAuthenticator.Authenticate(authorizationHeader, keySet)`**: parses `Authorization: Bearer <token>`, SHA-256-hashes the token, constant-time-compares (`CryptographicOperations.FixedTimeEquals`, no early-out) against each active entry's `SecretHash`. Empty set ⇒ `AuthorizedNoKey` (auth disabled). Match ⇒ authorized + tags the connection with that `keyId`.
- **`devices.json`** (`DevicesFileEnvelope`): `{ "schemaVersion": 1, "devices": [{ "keyId", "name", "secretHash" (SHA-256 hex), "createdAt" (ISO-8601), "revokedAt"? (ISO-8601|null) }] }`. `DeviceKeyStoreReader` excludes entries that are revoked or have blank `secretHash`.
- **`DeviceKeyStoreWatcher`** (hosted service): watches `devices.json`, 250 ms debounce; present+parseable ⇒ swap active set (empty array = operator disables auth); malformed/absent-at-runtime ⇒ keep last-good + warn; on swap, any `keyId` that left the active set has its live connections aborted (`INetworkConnection.Abort`) — revocation fencing (reuses ADR-012 fencing).
- **`Program.cs`**: absent file at startup ⇒ `DeviceKeySet.Empty` (auth disabled); malformed at startup ⇒ fatal.
- **`lastseen.json`** (`LastSeenWriter`, host-owned): `{ "schemaVersion": 1, "lastSeen": { "<keyId>": "<ISO-8601>" } }`, throttled (`LastSeenThrottleSeconds`, default 60), best-effort.

ADR-018 (Accepted) is the binding decision record. The standing spec `remote-session-gateway` already specifies this behaviour in its "Tailscale-fronted authentication" and "File-backed device-key store with hot reload" requirements — **this change does not alter that behaviour or those requirements**; it corrects the document that contradicts them.

## Goals / Non-Goals

**Goals:**
- The deploy guide accurately describes the shipped per-device-key auth: enrolment, `devices.json` schema + location, the `secretHash` = SHA-256 contract, revocation, hot-reload, and the empty-set-disables-auth / first-device-enables-enforcement semantics.
- Remove every `Network:SharedKey` / `NETWORK__SharedKey` reference (Step 1 examples, Step 4, the reference table) and the "one pre-shared key" prose.
- Preserve the Tailscale-as-boundary trust model framing (ADR-012): per-device keys are defense-in-depth on top of Tailscale, not a replacement.
- Give operators a concrete, shippable enrolment path (manual `devices.json` edit) and reference dmonium as the intended pairing surface only to the extent it actually ships.

**Non-Goals:**
- No code, config-schema, appsettings, or runtime-behaviour change. Auth behaviour is already correct and specified.
- No change to the `remote-session-gateway` behavioural requirements (the ADDED requirement is documentation-accuracy only).
- No new enrolment CLI / tooling (none exists; building one is a separate change).
- No rewrite of `docs/configuration.md` or `docs/protocol/README.md` (flag if they carry the same drift).
- No change to the Tailscale (Steps 2–3) or bind-policy material beyond the auth sections.

## Decisions

### D1: Documentation-only change, framed as an ADDED documentation requirement
The change touches exactly one file (`docs/deploying-the-network.md`). To remain a valid spec-driven OpenSpec change without misrepresenting it as a behaviour change, it ADDS one operator-documentation requirement to `remote-session-gateway` ("the deployment guide accurately documents the per-device key model and references no `SharedKey`") and modifies **no** existing requirement. The existing "Tailscale-fronted authentication" and "File-backed device-key store with hot reload" requirements are the source of truth for the behaviour and are left untouched.

### D2: Describe manual `devices.json` enrolment as the primary, concrete path
There is **no enrolment CLI** in the repo. The reliably-correct operator instruction is: hand-edit `~/.dmon/network/devices.json` to the documented schema, computing `secretHash` with `echo -n "<token>" | openssl dgst -sha256` (or `shasum -a 256`), and rely on hot-reload. dmonium (`daemon/Daemon.App`, ADR-018 Decision 4: operator owns `devices.json`, mint-on-pair, reads `lastseen.json`) is referenced as the intended pairing surface, but the doc must not over-claim QR/automated pairing if that surface is not shipped — **the worker verifies dmonium's actual capability at apply** and words it accordingly.

### D3: Keep the Tailscale trust-model framing; reposition the key as per-device
The "Trust model" section's two-factor framing (Tailscale node auth + an extra key check) stays — only the "single shared key" becomes "per-device keys." This preserves ADR-012's posture and avoids implying Tailscale is replaced.

### D4: Reference-table edits
Remove the `Network:SharedKey` row. Add `Network:DeviceKeyStoreDirectory` (default `~/.dmon/network/`; the store dir holding `devices.json` + `lastseen.json`) and `Network:LastSeenThrottleSeconds` (default 60). Verify the rest of the table against `NetworkOptions` at apply.

### D5: Verification is review + a doc-internal consistency check
Gates are `openspec validate --strict` plus a documentation review that every fact (schema field names, default paths, hash algorithm, fencing/hot-reload semantics, config keys) matches the shipped `frontends/Dmon.Network/` code and ADR-018. No build/test behaviour gate applies (doc-only), though `make build`/`make test` must remain green (they are unaffected).

## Risks / Trade-offs

- **[Speccing a document]** Adding a documentation requirement to a behavioural capability is mildly unconventional. Mitigation: it is explicitly an ADDED, documentation-accuracy requirement with no behavioural content; the behaviour stays owned by the existing requirements. If the reviewer judges it pollutes the spec, the alternative is to drop the OpenSpec wrapper and ship the doc fix as a plain PR — a stop-and-ask at apply.
- **[Dependency on #67]** The target file only exists once `gateway-packaging` (PR #67) merges. If #67's doc layout changes during review, the section/line references here go stale — re-confirm at apply.
- **[Over-claiming dmonium pairing]** If the doc describes automated QR pairing that isn't shipped, operators are misled again. Mitigation (D2): the worker verifies the actual dmonium surface and defaults to the manual path as primary.
- **[Adjacent drift left behind]** `docs/configuration.md` / `docs/protocol/README.md` may carry the same shared-key staleness; out of scope here, flagged for a follow-up so the fix isn't assumed complete repo-wide.

## Open Questions

- Does dmonium (`daemon/Daemon.App`) actually ship device pairing / `devices.json` minting today, or is ADR-018 Decision 4 still aspirational? (Worker confirms at apply; governs how strongly the doc leans on dmonium vs. manual editing.)
- Should `docs/configuration.md` / `docs/protocol/README.md` be folded in, or remain a separate follow-up? (Leaning separate — keep this change a single-file, low-risk correction.)
