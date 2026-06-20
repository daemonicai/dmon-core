# DEVLOG — daemon-app-management

Cross-block memory for the architect. Newest decisions at the bottom of each section.

## Pinned facts (read every block)

- **App boundary:** dmonium manages the **Gateway** process; the Gateway owns build+run+restart of the `Daemon.cs` core (`CoreLauncher`/`CoreProcessManager`). The app never builds/runs the core.
- **Core-restart wire command is DEFERRED** (out of scope). Settings-apply keeps bouncing the whole Gateway process. Do not add a wire command.
- **No `Daemon.cs` source-editing / Roslyn.** Settings are config-only via `IConfiguration`.
- **`DMON_` prefix is for dmon-core's OWN vars only.** Keep `DCAL_*`/`DMAIL_*` server vars (ADR-028) and `GEMINI_API_KEY` unchanged. `DMON_GATEWAY_PATH` already correct.
- **Gates:** `make build` (0 warn), `env -u MEKO_API_KEY make test` (the `env -u` avoids the live-Meko smoke hang), and for Swift blocks `make daemon-app` + `swift test` in `daemon/Daemon.App`, plus `openspec validate daemon-app-management --strict`.
- **Open question (still unresolved):** how the `services/Dcal` / `services/Dmail` executables are produced/launched (native exe vs `dotnet <dll>`) and their default install path. Group 3 (task 3.1) resolves it; until then the `ServerProcessManager` reports `unknown` ("not configured") when no path resolves.

## Block 1 — tasks 1.1–1.4 (Daemon.cs config refactor) — DONE

- `daemon/Daemon.cs`: `DmonHostBuilder` now constructed first; all six inference knobs read via `builder.Configuration.GetValue<string>(key, default)` before the `IChatClient`s. Defaults equal the prior literals byte-for-byte, so a no-env run is unchanged.
- Renamed `DCAL_E2B_URL`→`DMON_E2B_URL`, `DCAL_REASONER_URL`→`DMON_REASONER_URL`. Added model keys `DMON_E2B_MODEL`/`DMON_REASONER_MODEL`/`DMON_EGRESS_MODEL`. `GEMINI_API_KEY` read via config but key name unchanged.
- **`GetValue<T>` resolution:** transitive via `Microsoft.Extensions.Hosting` — only needed `using Microsoft.Extensions.Configuration;`. **No `#:package` directive.** (Future Swift blocks: no analog needed.)
- Reviewer: Approve, no blockers. ADR-019 (`#:project` + `RunAsync()` tail), ADR-027 (routing wiring), ADR-028 (server prefixes) all intact.
- **Known temporary contract drift (expected):** `SettingsView.swift` still reads/writes/labels `DCAL_E2B_URL`/`DCAL_REASONER_URL` in config.yaml — task **7.1** owns the Swift rename. Until group 7 lands, endpoints set via the Settings panel are written under the old keys and silently ignored by the core (defaults apply). Keep on the radar; made whole by 7.1.
