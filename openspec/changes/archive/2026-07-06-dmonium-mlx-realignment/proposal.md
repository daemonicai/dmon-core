## Why

ADR-032 (daemon-triage-escalation) dropped the upfront `Tier`/`Reasoner` dispatch in favour of first-line → `think_harder` → escalation, and ADR-034 (mlx-local-runtime) replaced the oMLX/Ollama backends with the `Dmon.Providers.Mlx` runtime — first-line `gemma-4-e4b` on fixed port 8800 and escalation `gemma-4-26B` on fixed port 8810, both provider-spawned with baked fixed ports/model ids (not env-configured URLs). The **core** `daemon/Daemon.cs` was updated, but the **dmonium** app (`daemon/Daemon.App`, Swift) and the `daemon-host` / `daemon-composition-root` standing specs and daemon docs were not — they still expose a "reasoner" tier and the obsolete "two configurable HTTP endpoints (e2b + reasoner)" model that no longer exists. dmonium's settings still offer Reasoner/E2B endpoint+model fields, and its health dashboard still probes a "Reasoner Endpoint" and an "E2B Endpoint" (defaulting to Ollama) that the shipped core never serves.

## What Changes

- **dmonium drops the obsolete inference-endpoint surface.** Remove the `DMON_E2B_URL` / `DMON_REASONER_URL` / `DMON_E2B_MODEL` / `DMON_REASONER_MODEL` settings fields from `SettingsView.swift` and the `E2B Endpoint` + `Reasoner Endpoint` health probes from `DaemonController.swift`. **BREAKING** to dmonium's settings/health surface (no production deployments; clean break). The core (`Dmon.Providers.Mlx`) owns the mlx runtimes' lifecycle and readiness, so dmonium probing local inference endpoints is redundant — see design D1 for the alternatives weighed.
- **dmonium keeps everything else unchanged:** the egress model + threshold (`DMON_EGRESS_MODEL`, `DMON_EGRESS_THRESHOLD`), `GEMINI_API_KEY`, the Dcal/Dmail/Network server paths, Tailscale bring-up, and the Dcal/Dmail/Tailscale/calendar-sync/Network/egress health components it already supervises.
- **Swift tests realigned** (`ConfigStoreTests.swift`) to drop assertions on the removed vars.
- **Standing specs synced to shipped reality:** `daemon-host` (settings-fields + health-probe requirements) and `daemon-composition-root` (the triage-backend wiring requirement now describes `AddMlxFirstline`/`AddMlxEscalation` + `UseTriage`/`AddEscalation`/`AddEgress`/`AddEscalationWarming`; the credentials requirement reflects that only `GEMINI_API_KEY` + `DMON_EGRESS_MODEL` are env-sourced, with the mlx runtimes using the provider's baked fixed-port/quant defaults).
- **Docs:** `daemon/BRIEF.md` gets a prominent superseded/historical banner (its `Tier`/`Reasoner`/`AddReasoner`/Ollama design was superseded by ADR-032/ADR-034); `daemon/Daemon.App/README.md` health/settings prose updated to drop the dead env-var names.

Out of scope: no core or `Dmon.Providers.*` code changes (the core is already correct); no new ADR (this conforms the daemon surface to already-accepted ADR-032/ADR-034); no redesign of dmonium to probe the mlx fixed ports (design D1 rejects that).

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `daemon-host`: the dmonium settings-fields requirement and the model-runner health-probe requirement drop the `DMON_E2B_URL`/`DMON_REASONER_URL` (+ `_MODEL`) inference-endpoint fields and the E2B/Reasoner endpoint probes; dmonium no longer health-probes local inference endpoints (the core owns mlx runtime health). Egress + Dcal/Dmail/Tailscale/calendar-sync/Network components are unchanged.
- `daemon-composition-root`: the triage-routing-backends requirement is restated for the shipped composition (`AddMlxFirstline`/`AddMlxEscalation` + `UseTriage`/`AddEscalation`/`AddEgress`/`AddEscalationWarming`, replacing the `AddReasoner` wording); the credentials requirement reflects that only `GEMINI_API_KEY` + `DMON_EGRESS_MODEL` are env-sourced and the mlx runtimes use baked provider defaults (the `DMON_E2B_*`/`DMON_REASONER_*` env keys are removed).

## Impact

- **dmonium (Swift):** `daemon/Daemon.App/Sources/DaemonApp/SettingsView.swift`, `daemon/Daemon.App/Sources/DaemonApp/DaemonController.swift`, `daemon/Daemon.App/Tests/DaemonAppTests/ConfigStoreTests.swift`. Built/tested via `make daemon-app` (outside `Everything.slnx`).
- **Standing specs:** `openspec/specs/daemon-host/spec.md`, `openspec/specs/daemon-composition-root/spec.md`.
- **Docs:** `daemon/BRIEF.md`, `daemon/Daemon.App/README.md`.
- **Config keys removed:** `DMON_E2B_URL`, `DMON_REASONER_URL`, `DMON_E2B_MODEL`, `DMON_REASONER_MODEL` (dmonium side). Retained: `DMON_EGRESS_MODEL`, `DMON_EGRESS_THRESHOLD`, `GEMINI_API_KEY`, `DMON_DCAL_SERVER_PATH`, `DMON_DMAIL_SERVER_PATH`, `DMON_NETWORK_PATH`.
- **No core/provider/protocol changes.** No ADR change.
