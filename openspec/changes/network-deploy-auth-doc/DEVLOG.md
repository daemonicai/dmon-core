# DEVLOG — network-deploy-auth-doc

Documentation-only follow-up to gateway-packaging. One block, one commit. Branch `change/network-deploy-auth-doc` (based on main post-#67).

## Block — tasks 1.1–3.3 (DONE)

Rewrote the auth material in `docs/deploying-the-network.md` from the obsolete single-`Network:SharedKey` model to the shipped per-device-key model (ADR-018). Single file, 117 insertions / 26 deletions.

- **Load-bearing investigation (1.2):** dmonium (`daemon/Daemon.App/Sources/`) ships **NO** device pairing / `devices.json` minting / QR today — `NetworkManager.swift` only supervises the host process. ADR-018 Decision 4 (dmonium mint-on-pair) is aspirational. → Doc leads with the **manual `devices.json` hand-edit** path; dmonium is framed as intended-but-not-yet-shipped (no working QR flow claimed). This avoids re-misleading the operator (the exact defect class fixed).
- **1.1:** all `design.md` Context facts verified against shipped code — no drift.
- Rewrote: Trust model (per-device keys, two-factor framing on top of Tailscale kept — ADR-012), Step 1 examples (removed `NETWORK__SharedKey` env + `SharedKey` JSON), Step 4 → "per-device key enrolment" (`devices.json` schema + example, `~/.dmon/network/` default + `DeviceKeyStoreDirectory` override, `secretHash`=SHA-256 hex via `openssl dgst -sha256`, empty/absent⇒disabled / first-device⇒enforced, revocation via `revokedAt` fences live connections, 250ms-debounced hot-reload, fail-closed-to-last-good at runtime / fatal at startup), Verifying connectivity (device-token framing: absent⇒401, valid⇒101), reference table (removed `Network:SharedKey`; added `DeviceKeyStoreDirectory`, `LastSeenThrottleSeconds`, `CreateHandshakeTimeoutSeconds`, `WorkspaceRoot` to match the full `NetworkOptions` field set).
- **Reviewer:** all facts verified exact against `frontends/Dmon.Network/` + ADR-018; one blocker — a transposed ADR-018 link slug (`ADR-018-gateway-per-device-keys.md` → `ADR-018-per-device-gateway-keys.md`), fixed by orchestrator + verified (all 3 ADR links resolve).
- **Confirmed no drift elsewhere:** `docs/configuration.md` / `docs/protocol/README.md` carry no `SharedKey` references (only ADR-018/033 historical refs, which are correct) — no follow-up owed there.
- Gates: grep gate zero `SharedKey`; `openspec validate --strict` passes; `make build`/`make test` green (regression-only — doc-only change).
