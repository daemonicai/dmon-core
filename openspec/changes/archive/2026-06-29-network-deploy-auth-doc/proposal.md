## Why

`docs/deploying-the-network.md` documents authentication as a single pre-shared bearer key (`Network:SharedKey` / `NETWORK__SharedKey`, "Step 4 — Optional: shared-key defense-in-depth"). That model no longer exists: ADR-018 replaced the scalar shared key with a **per-device revocable key set**, and the shipped `Dmon.Network` host has **no `SharedKey` option** — `NetworkOptions` exposes `DeviceKeyStoreDirectory`, and auth runs through `DeviceKeyAuthenticator` against a file-backed `devices.json`. The `gateway-packaging` rename (PR #67) faithfully carried the stale shared-key prose across `deploying-the-gateway.md` → `deploying-the-network.md` without re-validating it against the code. The deploy guide therefore instructs operators to configure a key that does nothing, and omits the real enrolment path — a correctness defect in the one document that tells an operator how to stand the host up.

## What Changes

Documentation-only. No code, config schema, or runtime behaviour changes.

- **Rewrite the auth material in `docs/deploying-the-network.md`** to describe the shipped per-device-key model (ADR-018):
  - Replace the "Trust model" shared-key prose and "Step 4 — shared-key defense-in-depth" with per-device key enrolment.
  - Document the `devices.json` store: default location `~/.dmon/network/devices.json` (override via `Network:DeviceKeyStoreDirectory`), the `{ schemaVersion, devices: [{ keyId, name, secretHash, createdAt, revokedAt? }] }` schema, and that `secretHash` is the **SHA-256 hex of the device token** (never the token itself).
  - Document the auth semantics: empty/absent store ⇒ auth disabled (open over the tailnet, operator cannot lock themselves out); a presented `Authorization: Bearer <token>` is SHA-256'd and constant-time-compared against active (non-revoked) entries; first paired device flips enforcement on.
  - Document **revocation** (set `revokedAt` on the entry → live connections for that `keyId` are fenced/aborted) and **hot-reload** (the host watches `devices.json`; pairing/revocation take effect without restart; malformed file ⇒ fail-closed to last-good).
  - Document the **manual enrolment fallback** (hand-edit `devices.json`; compute `secretHash` via `echo -n "<token>" | openssl dgst -sha256`) as the concrete, shippable path, and reference dmonium (`daemon/Daemon.App`) as the intended operator surface for pairing **only to the extent that surface actually ships** (verified at apply — do not over-claim QR/automated pairing if it is not implemented).
  - Fix the **"Verifying connectivity"** examples that show `Authorization: Bearer wrong` against a shared key so they reflect device-token auth.
  - Update the **Reference** config table: remove the `Network:SharedKey` row; add `Network:DeviceKeyStoreDirectory` and `Network:LastSeenThrottleSeconds`.
- Keep the **Tailscale-as-boundary** trust model intact (ADR-012) — per-device keys are defense-in-depth on top of Tailscale, exactly as the shared key was framed.

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `remote-session-gateway`:
  - **ADD** an operator-documentation requirement: the deployment guide SHALL accurately document the per-device key model (enrolment, `devices.json` schema/location, revocation, hot-reload) and SHALL NOT reference a single pre-shared `SharedKey`. This is a documentation-accuracy requirement; it adds **no** new system behaviour — the auth behaviour is already specified by the existing "Tailscale-fronted authentication" and "File-backed device-key store with hot reload" requirements, which this change does not modify.

## Impact

- **Docs:** `docs/deploying-the-network.md` (auth sections + reference table). No other file changes; no code, csproj, appsettings, or spec-behaviour changes.
- **Sequencing / dependency:** this change edits `docs/deploying-the-network.md`, which is **created by `gateway-packaging` (PR #67)**. It must land **after #67 merges**; the proposal branch is based on the gateway-packaging branch and should be rebased onto `main` once #67 is in. (If #67 is reworked, re-confirm the doc's section/line layout before applying.)
- **Verification at apply:** the worker must confirm against the shipped code/dmonium what enrolment tooling actually exists. There is currently **no enrolment CLI** in the repo; if dmonium's automated pairing is not shipped, the doc describes the manual `devices.json` path as primary and references dmonium as the intended surface without over-claiming.
- **Related drift not in scope:** other docs that may describe shared-key auth (e.g. `docs/configuration.md`, `docs/protocol/README.md`) are out of scope here — flag if encountered.
