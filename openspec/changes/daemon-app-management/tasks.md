## 1. Daemon.cs config refactor (C#)

- [x] 1.1 Reorder `daemon/Daemon.cs` so `DmonHostBuilder builder = DmonHost.CreateBuilder(args)` is created before the `IChatClient`s, and read endpoints/key/model-IDs via `builder.Configuration.GetValue<string>(key, default)` (confirm `Microsoft.Extensions.Configuration.Binder` is available transitively; add the `using` if needed)
- [x] 1.2 Parameterize the three model IDs from config: `DMON_E2B_MODEL` (default `gemma4:e2b-it-qat`), `DMON_REASONER_MODEL` (default `gemma4-27b`), `DMON_EGRESS_MODEL` (default `gemini-2.5-flash`)
- [x] 1.3 Apply the `DMON_` rename for dmon-core's inference endpoints: `DCAL_E2B_URL`→`DMON_E2B_URL`, `DCAL_REASONER_URL`→`DMON_REASONER_URL`; leave `GEMINI_API_KEY`, `DMON_GATEWAY_PATH`, and all `DCAL_*`/`DMAIL_*` server vars unchanged
- [x] 1.4 Verify `make build` is clean (0 warnings) and `env -u MEKO_API_KEY make test` is green

## 2. Reusable server process manager (Swift)

- [x] 2.1 Extract the Gateway lifecycle into a reusable `ServerProcessManager` (display name, executable-resolution candidates, launch args, PID-file path, env dict) covering launch, PID-file adopt, exponential back-off (2s→60s), intentional-stop guard, and terminate-on-quit
- [x] 2.2 Re-express `GatewayManager` in terms of `ServerProcessManager` configured with `--agent daemon/Daemon.cs`, preserving its current behavior and `DMON_GATEWAY_PATH` resolution
- [x] 2.3 Factor the PID-liveness check behind an injectable probe so it is unit-testable without spawning a process

## 3. Dcal and Dmail server supervision (Swift)

- [x] 3.1 Confirm how the `services/Dcal` and `services/Dmail` executables are produced/launched and choose the default resolution candidate(s); wire config-overridable path keys `DMON_DCAL_SERVER_PATH` / `DMON_DMAIL_SERVER_PATH`
- [x] 3.2 Instantiate a `ServerProcessManager` for the Dcal server and one for the Dmail server, injecting settings-derived env; when no executable resolves, report `unknown` ("not configured") and do not spawn
- [x] 3.3 Start/adopt supervised servers on app launch and terminate them on quit, alongside the Gateway (terminate-on-quit satisfied via OS process-group inheritance; explicit orderly teardown + PID cleanup tracked as task 10.1)

## 4. Unified health model and registry (Swift)

- [x] 4.1 Add `ComponentHealth { name, status, detail? }` with `HealthStatus = ok | degraded | down | unknown`
- [x] 4.2 Add a `HealthRegistry: ObservableObject` that collects each component's `ComponentHealth` into an ordered list plus an aggregate rollup (gateway-stopped→red; any `down`→red; any `degraded`/`unknown`→amber; else green)
- [x] 4.3 Adapt `GatewayManager`, `TailscaleMonitor`, `DcalHealthMonitor`, and the new server managers to publish `ComponentHealth` into the registry

## 5. Dmail and model-runner endpoint health (Swift)

- [x] 5.1 Add a Dmail health probe (mirror `DcalHealthMonitor` against `DMAIL_BASE_URL`/`DMAIL_API_KEY`) publishing the Dmail `ComponentHealth`
- [x] 5.2 Add a reusable `EndpointHealthProbe` that classifies each configured inference endpoint (`DMON_E2B_URL`, `DMON_REASONER_URL`, egress target) as `ok` on any HTTP response, `down` otherwise — no per-runner special-casing
- [x] 5.3 Register the configured endpoints as health components in the registry
- [x] 5.4 Keep status classification (Tailscale and endpoint) as pure functions over decoded inputs so they are unit-testable

## 6. Menu and icon aggregate; Tailscale bring-up (Swift)

- [x] 6.1 Update `MenuBarView` to render one row per registered `ComponentHealth`
- [x] 6.2 Drive the `DaemonApp` status-icon color from the `HealthRegistry` aggregate rollup
- [ ] 6.3 Add a best-effort "Bring Tailscale up" menu action that runs `tailscale up` and reflects the outcome in the Tailscale row (no interactive auth flow)
- [ ] 6.4 Add an `NSApplicationDelegateAdaptor` whose `applicationWillTerminate` calls `stop()` on the Gateway, Dcal, and Dmail managers — an explicit orderly terminate + PID-file cleanup on quit, making "terminate on quit" literal for all three managers (currently relies on OS process-group death only; PID files survive a quit)

## 7. Settings panel (Swift)

- [ ] 7.1 Apply the `DMON_` env rename in `SettingsView` load/persist/help text: `DCAL_E2B_URL`→`DMON_E2B_URL`, `DCAL_REASONER_URL`→`DMON_REASONER_URL`, `DAEMON_EGRESS_THRESHOLD`→`DMON_EGRESS_THRESHOLD`
- [ ] 7.2 Add fields for the three model IDs (`DMON_E2B_MODEL`/`DMON_REASONER_MODEL`/`DMON_EGRESS_MODEL`) and the two server paths (`DMON_DCAL_SERVER_PATH`/`DMON_DMAIL_SERVER_PATH`), persisted via the existing config.yaml + Keychain mechanism
- [ ] 7.3 Confirm settings-apply continues to restart the Gateway (core-restart wire command remains out of scope)

## 8. Swift tests (XCTest)

- [ ] 8.1 Add a `DaemonAppTests` test target to `daemon/Daemon.App/Package.swift`
- [ ] 8.2 Test `ServerProcessManager` PID adoption against an injected liveness probe (live vs dead)
- [ ] 8.3 Test health classification: Tailscale `up`/`degraded`/`down`, endpoint reachable/unreachable, and the `HealthRegistry` aggregate rollup
- [ ] 8.4 Test `ConfigStore` flat-YAML read/write round-trip and the settings env-key mapping (incl. `DMON_` keys and secret-omission rules)

## 9. Sync and gates

- [ ] 9.1 Update the `daemon/Daemon.App/README.md` layout/notes if the source set changed (new managers, health, tests)
- [ ] 9.2 Run all gates: `make build` (0 warnings), `env -u MEKO_API_KEY make test`, `make daemon-app` (swift build clean), `swift test` (in `daemon/Daemon.App`), and `openspec validate daemon-app-management --strict`
