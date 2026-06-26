## 1. Verify the shipped auth model before writing

- [ ] 1.1 Confirm against `frontends/Dmon.Network/` that the facts in `design.md` Context still hold: `NetworkOptions` has no `SharedKey` and exposes `DeviceKeyStoreDirectory`/`LastSeenThrottleSeconds`; `DeviceKeyAuthenticator` does Bearer-token → SHA-256 → constant-time compare; `devices.json` schema = `{ schemaVersion, devices: [{ keyId, name, secretHash, createdAt, revokedAt? }] }`; default store `~/.dmon/network/`; `DeviceKeyStoreWatcher` hot-reload + revocation fencing + fail-closed-to-last-good; empty/absent ⇒ auth disabled. Note any drift from `design.md` and adjust.
- [ ] 1.2 Determine what enrolment surface dmonium (`daemon/Daemon.App`) actually ships (does it mint keys / write `devices.json` / do QR pairing today?). This decides how strongly the doc leans on dmonium vs. the manual `devices.json` path (design D2). If dmonium pairing is not implemented, the manual path is primary and dmonium is referenced as intended-but-not-yet-shipped.

## 2. Rewrite the auth content of `docs/deploying-the-network.md`

- [ ] 2.1 "Trust model" section: keep the Tailscale-as-boundary + defense-in-depth framing (ADR-012); replace "single shared key" prose with per-device keys.
- [ ] 2.2 Step 1 examples: remove the `NETWORK__SharedKey="$(openssl rand -hex 32)"` env example and the `"SharedKey": "<your-secret>"` JSON example; if an auth example is shown, show the device-key store path / `DeviceKeyStoreDirectory` instead.
- [ ] 2.3 Replace "Step 4 — Optional: shared-key defense-in-depth" with per-device key enrolment: the `devices.json` schema + example, default location `~/.dmon/network/devices.json` (override `Network:DeviceKeyStoreDirectory`), `secretHash` = `echo -n "<token>" | openssl dgst -sha256` (or `shasum -a 256`), empty/absent ⇒ disabled / first device ⇒ enforced, revocation via `revokedAt` (fences live connections), and hot-reload (no restart; malformed ⇒ fail-closed to last-good). Reference dmonium per task 1.2's finding without over-claiming.
- [ ] 2.4 "Verifying connectivity": update the `Authorization: Bearer wrong` / shared-key examples to device-token auth (a token absent from `devices.json` ⇒ 401; a valid device token ⇒ upgrade). Optionally mention `lastseen.json` as the host-written activity record.
- [ ] 2.5 Reference table: remove the `Network:SharedKey` row; add `Network:DeviceKeyStoreDirectory` (default `~/.dmon/network/`) and `Network:LastSeenThrottleSeconds` (default 60); verify every remaining row against `NetworkOptions`.
- [ ] 2.6 Fix inbound links / cross-references if any other shipping doc points at the auth section (do not edit `docs/configuration.md` / `docs/protocol/README.md` — flag if they carry the same drift).

## 3. Validate

- [ ] 3.1 Grep gate: no `SharedKey` / `NETWORK__SharedKey` / `Network:SharedKey` references remain in `docs/deploying-the-network.md`.
- [ ] 3.2 Documentation review: every documented fact (schema field names, default paths, SHA-256, revocation/fencing, hot-reload, config keys) matches `frontends/Dmon.Network/` and ADR-018.
- [ ] 3.3 `openspec validate network-deploy-auth-doc --strict` passes; `make build` / `env -u MEKO_API_KEY make test` remain green (unaffected — doc-only).
