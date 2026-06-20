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

## Block 2 — tasks 2.1–2.3 (ServerProcessManager extraction) — DONE

- New `ServerProcessManager.swift`: `ServerProcessConfig` value type (`displayName`, `executableCandidates: [() -> String?]` (lazy closures, re-evaluated at start time), `arguments`, `pidFileURL`, `baseEnvironment`) + `@MainActor ServerProcessManager: ObservableObject` with the full lifecycle (adopt-via-PID → launch, back-off 2→60 reset-to-2, intentional-stop guard, terminate-via-`stop()`, env merge).
- **CORRECTION (per Block-3 review):** the manager has NO explicit terminate-on-quit / `deinit` / app-lifecycle teardown. A child process only gets `stop()`+PID-cleanup on an explicit `stop()` call. On app quit, children die via OS **process-group inheritance** only, and PID files survive the quit (benign: `adoptIfRunning` gates on `kill(pid,0)`). Explicit orderly teardown is now tracked as **task 6.4**. Injectable `typealias PidLivenessProbe = (Int32) -> Bool`, default `{ kill($0,0)==0 }`, `var livenessProbe` seam; `adoptIfRunning()` (now `internal` for group-8 testing) calls through it.
- `GatewayManager.swift` rewritten as a thin wrapper owning a `ServerProcessManager` (displayName "Gateway", candidate order override→`DMON_GATEWAY_PATH`→`~/.dotnet/tools/Dmon.Gateway`, args `["--agent","daemon/Daemon.cs"]`, PID `~/.dmon/run/gateway.pid`). `gatewayPathOverride.didSet` rebuilds candidates; `settingsEnvironment.didSet` → `manager.additionalEnvironment`; `isRunning`/`lastExitCode` mirrored via Combine `.receive(on: RunLoop.main).assign(to:)`. Public surface unchanged — the 3 consumer files compile unedited.
- Reviewer: Approve, no blockers, full parity table confirmed.
- **Note for group 3 (architect, important):** the manager has a TWO-TIER env split — `config.baseEnvironment` (static per-server env) vs `manager.additionalEnvironment` (live caller-mutated overlay; `GatewayManager` routes `settingsEnvironment` here). When wiring Dcal/Dmail: put per-server STATIC env in `baseEnvironment`, reserve `additionalEnvironment` for the live overlay. Don't conflate them.
- **Build gotcha:** `swift build` cache can report "Build complete (0.11s)" without recompiling after edits, and SourceKit diagnostics can be stale mid-edit (e.g. "Cannot find type X in scope" for same-module types — single-file analysis lacks module context). To verify a Swift block for real, `rm -rf daemon/Daemon.App/.build` before `make daemon-app`.

## Block 3 — tasks 3.1–3.3 (Dcal/Dmail server supervision) — DONE

- **Open question RESOLVED:** `services/Dcal` and `services/Dmail` are `Microsoft.NET.Sdk.Web` projects emitting a NATIVE apphost exe at `services/<Name>/bin/Release/net10.0/<Name>` (`Dcal`/`Dmail`; `AssemblyName` matches). Launched DIRECTLY like the Gateway (no `dotnet <dll>`), no arguments. Servers read their config (`DCAL_ICAL_URL` required, `DMAIL_*`, …) from ENV.
- New `ServiceManager.swift`: ONE parameterised `@MainActor ObservableObject` wrapper over `ServerProcessManager` (mirrors `GatewayManager`), with `makeDcal()`/`makeDmail()` factories. PID files `~/.dmon/run/dcal.pid` / `dmail.pid`. `baseEnvironment: [:]` (inherited env carries `DCAL_*`/`DMAIL_*`); `settingsEnvironment` seam → `additionalEnvironment` reserved for group 7. Exposes `pathOverride` seam (group 7).
- **Resolution candidates: override → `DMON_DCAL_SERVER_PATH`/`DMON_DMAIL_SERVER_PATH` env → NO default.** A menu-bar app launched from Finder/login-item has cwd `/`, so the repo build-output path can't be anchored at runtime; baking a wrong default would violate Decision 2. Unresolved → no spawn (`isRunning=false`, no throw, no back-off, no PID write) = the designed "not configured" state. Group 4 renders `isRunning==false && lastExitCode==nil` as `unknown`, distinct from a crash (`lastExitCode != nil`).
- `DaemonApp.swift`: two new `@StateObject`s, `.environmentObject`'d into `MenuBarView` (for groups 4/6), started in the existing `.task`. `MenuBarView`/`SettingsView` compile unedited.
- **`DMON_` vs `DCAL_`/`DMAIL_` prefix:** only the two app-owned PATH keys are `DMON_`; servers' own config keys stay `DCAL_*`/`DMAIL_*` (ADR-028).
- **Terminate-on-quit RULING (orchestrator, conscious call):** the spec scenario "every supervised server child process it started is terminated" is judged SATISFIED by OS process-group death (the scenario asserts child termination, not an explicit `stop()`), consistent with the already-approved Gateway. Stale PID files on quit are benign (`adoptIfRunning` → `kill(pid,0)`). 3.3 ticked on this basis. Explicit orderly teardown (NSApplicationDelegateAdaptor → `stop()` all three + PID cleanup) is deferred to **task 6.4** (cross-cutting, covers Gateway too — not bolted partial onto Block 3).
- Reviewer: Approve, no blockers; all decisions/bindings COMPLIANT.

## Block 4 — tasks 4.1–4.3 (unified health model + registry) — DONE

- New `ComponentHealth.swift`: `HealthStatus` enum (ok/degraded/down/unknown), `ComponentHealth {name,status,detail?}` (Equatable), and TWO pure free functions — `processHealth(isRunning:lastExitCode:)` and `tailscaleHealth(status:)` — unit-testable without an ObservableObject (group 8 / task 8.3).
- New `HealthRegistry.swift`: `@MainActor ObservableObject` with ordered `components: [ComponentHealth]` (slots keyed by integer `order`, rebuilt sorted on every event → display order Gateway0/Dcal1/Dmail2/Tailscale3/Calendar-Sync4, timing-independent), `rollupColor: RollupColor`, a `register(publisher:order:)` Combine API + `observeGatewayStopped(_:)`, and the PURE `static func rollup(gatewayStopped:components:) -> RollupColor`.
- **Rollup precedence (verified):** `gatewayStopped → .red`; else any `.down → .red`; else any `.degraded`/`.unknown` → `.amber`; else `.green`. A `degraded` never masks a `down` (down scan returns first).
- **Gateway-stopped override is FIRST-CLASS in the rollup signature**, NOT a component-status hack: `GatewayManager.componentHealth` stays honest (ok/down/unknown via `processHealth`); the red-forcing flows through the `gatewayStopped` param, fed by `observeGatewayStopped(gateway.$isRunning.map { !$0 })`. **Group 6 (task 6.2) must feed `gatewayStopped` + consume `rollupColor` for the icon.**
- Each monitor gained `@Published private(set) var componentHealth` (initial `.unknown` before first event). `DcalHealthMonitor` publishes a **"Calendar Sync"** component (its `/health` poll success→ok / nil→down) — DISTINCT from the Dcal SERVER-PROCESS component (which comes from the Dcal `ServiceManager`). No new probe; no group-5 bleed.
- **Minimal `DaemonApp` touch only:** `@StateObject healthRegistry`, registry wiring in `.task`, `.environmentObject(healthRegistry)` on `MenuBarView`. `iconColor` + the 3 `MenuBarView` rows UNCHANGED (group 6 swaps them).
- Reviewer: Approve; rollup ordering + gateway override correct; line-117 type-check-timeout was a stale mid-edit SourceKit artifact (actual line is a trivial `contains(where:)`). Fixed one doc-comment nit (slots keyed by `order`, not `name`).
