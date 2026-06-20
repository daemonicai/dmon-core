## Why

The dmonium menu-bar app (`daemon/Daemon.App`) today only manages the Gateway process and shows Gateway/Tailscale/Dcal status. It cannot start or supervise the Dcal and Dmail **server** processes, has no health surface for Dmail or the local model runners, and its inference settings are mis-prefixed (`DCAL_*`) and partly un-wired (the three model IDs are hardcoded literals in `Daemon.cs`, so "change of model" is impossible without editing source). This makes dmonium a partial control surface for a stack it is meant to fully operate from the menu bar.

## What Changes

- **Server process management:** generalize the Gateway's launch / PID-adopt / back-off / stop pattern into a reusable manager and use it to supervise the **Dcal** and **Dmail** server processes (standalone artifacts under `services/`), with config-overridable binary paths.
- **Unified health:** a typed health registry aggregating Gateway, core (via Gateway), Dcal, **Dmail (new probe)**, Tailscale, and the **configured model-runner endpoints** (probe `DMON_E2B_URL` / `DMON_REASONER_URL` / egress target for reachability — no per-runner special-casing). Menu rows and the status-icon decision table reflect the aggregate. Adds a best-effort "Bring Tailscale up" menu action.
- **Config-only settings (no source editing):** `Daemon.cs` reads **all** knobs — including the three model IDs — via `IConfiguration` (env or `~/.dmon/config.yaml`). Settings panel gains model fields and server-path fields. **BREAKING (pre-release, no migration):** rename dmon-core's own env vars to the `DMON_` prefix — `DCAL_E2B_URL`→`DMON_E2B_URL`, `DCAL_REASONER_URL`→`DMON_REASONER_URL`, `DAEMON_EGRESS_THRESHOLD`→`DMON_EGRESS_THRESHOLD`; add `DMON_E2B_MODEL` / `DMON_REASONER_MODEL` / `DMON_EGRESS_MODEL`. `DCAL_*` / `DMAIL_*` **server** vars and provider keys (`GEMINI_API_KEY`) are unchanged.
- **Swift tests:** add a `DaemonAppTests` XCTest target covering process adoption, health classification, config round-trip, and env-key mapping.

## Capabilities

### New Capabilities
<!-- none — the dmonium app capability already exists as daemon-host -->

### Modified Capabilities
- `daemon-host`: add multi-server (Dcal/Dmail) process supervision; add a unified health surface (Dmail + model-runner-endpoint probes) and aggregate status icon; add the Tailscale bring-up action; settings manage the `DMON_`-prefixed inference/model keys and server-path keys.
- `daemon-composition-root`: the three inference **model IDs** are read from configuration (not hardcoded literals); dmon-core's own inference settings use the `DMON_` env prefix.

## Impact

- **Code:** `daemon/Daemon.cs` (builder-first reorder; `IConfiguration.GetValue` for all knobs; model-ID params; `DMON_` rename). `daemon/Daemon.App/Sources/DaemonApp/` (new `ServerProcessManager`, `HealthRegistry`, Dmail + endpoint probes; `SettingsView`, `MenuBarView`, `DaemonApp` updates). `daemon/Daemon.App/Package.swift` (+ test target).
- **No changes** to `core/`, `frontends/`, the RPC/wire protocol, or any ADR. The gateway core-restart wire command is **deferred**: settings-apply continues to bounce the whole Gateway process.
- **Binding ADRs honored:** ADR-028 (`daemon/`+`services/` buckets, keep `DCAL_*`/`DMAIL_*` server prefixes, Swift outside `Everything.slnx`), ADR-027 (routing in `Daemon.Routing`), ADR-019 (Gateway builds+runs the file-based core).
- **Out of scope:** core-restart wire command; any `Daemon.cs` source-editing / Roslyn helper; provider-key or server-var renames; deep Tailscale auth/login flows.
