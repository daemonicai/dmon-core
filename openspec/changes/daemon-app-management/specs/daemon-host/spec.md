## MODIFIED Requirements

### Requirement: Menu bar application shows live Gateway status
`Daemon.App` SHALL display a status icon in the macOS menu bar reflecting an aggregate rollup of all supervised and monitored components (Gateway, Dcal server, Dmail server, Tailscale, and the configured model-runner endpoints). Rollup: if the Gateway is stopped, the icon is red; otherwise if any component is `down`, the icon is red; otherwise if any component is `degraded` or `unknown`, the icon is amber; otherwise green.

#### Scenario: All components healthy shows green icon
- **WHEN** the Gateway is running, Tailscale is up, and every other monitored component reports `ok`
- **THEN** the menu bar icon is in the green state

#### Scenario: Gateway stopped shows red icon
- **WHEN** the Gateway process has exited or was never started
- **THEN** the menu bar icon is in the red state

#### Scenario: A monitored dependency down shows red icon
- **WHEN** the Gateway is running but a monitored component (e.g. the Dmail server or a configured model-runner endpoint) reports `down`
- **THEN** the menu bar icon is in the red state

#### Scenario: Tailscale degraded shows amber icon
- **WHEN** the Gateway is running and no component is `down`, but Tailscale reports `degraded` or a component is `unknown` (not configured)
- **THEN** the menu bar icon is in the amber state

---

### Requirement: Settings panel manages all Daemon configuration
The settings panel SHALL expose fields for: the Gemini API key; the Dmail base URL and API key; the Calendar iCal URL and API key; the `DMON_`-prefixed inference endpoints (`DMON_E2B_URL`, `DMON_REASONER_URL`); the three model IDs (`DMON_E2B_MODEL`, `DMON_REASONER_MODEL`, `DMON_EGRESS_MODEL`); the egress confidence threshold (`DMON_EGRESS_THRESHOLD`); the calendar sync interval; and the Dcal/Dmail server binary paths (`DMON_DCAL_SERVER_PATH`, `DMON_DMAIL_SERVER_PATH`). On save, secrets are stored in Keychain, non-secret values are written to `~/.dmon/config.yaml`, and the Gateway is signalled to restart so new settings take effect. Settings for dmon-core itself use the `DMON_` env prefix; `DCAL_*`/`DMAIL_*` server vars and provider keys are unchanged.

#### Scenario: API keys stored in Keychain
- **WHEN** an API key is saved in the settings panel
- **THEN** it is stored in the macOS Keychain (not plaintext in config.yaml)

#### Scenario: Non-secret settings written to config.yaml
- **WHEN** a non-secret setting (endpoint URL, model ID, server path, sync interval) is saved
- **THEN** it is written to `~/.dmon/config.yaml` under its `DMON_`-prefixed key

#### Scenario: Model ID is configurable without editing source
- **WHEN** a model ID field (e.g. `DMON_E2B_MODEL`) is saved and the Gateway restarts the core
- **THEN** the restarted core uses the configured model ID, with no edit to `Daemon.cs`

#### Scenario: Save triggers core session restart
- **WHEN** settings are saved
- **THEN** the Gateway is signalled to restart the active core session so new settings take effect

## ADDED Requirements

### Requirement: Daemon.App supervises the Dcal and Dmail server processes
`Daemon.App` SHALL supervise the Dcal server and the Dmail server using the same lifecycle as the Gateway: resolve the executable, adopt a live process via its PID file or launch a new one, restart on unexpected exit with exponential back-off (initial 2s, max 60s), inject settings-derived environment, and terminate the child when the app quits. The executable SHALL be resolved from a config-overridable path key (`DMON_DCAL_SERVER_PATH` / `DMON_DMAIL_SERVER_PATH`); when no executable resolves, the server SHALL be reported as `unknown` ("not configured") and SHALL NOT be spawned.

#### Scenario: Configured server launched on app start
- **WHEN** `Daemon.App` launches and a server's binary path resolves to an executable
- **THEN** that server process is started (or an already-running instance is adopted via its PID file)

#### Scenario: Crashed server is restarted with back-off
- **WHEN** a supervised server process exits unexpectedly
- **THEN** it is restarted after a back-off delay that doubles up to a 60-second cap

#### Scenario: Unresolved server path is not configured
- **WHEN** a server's binary path does not resolve to an executable
- **THEN** that server is reported with status `unknown` and no process is spawned

#### Scenario: Supervised servers terminate when app quits
- **WHEN** `Daemon.App` quits
- **THEN** every supervised server child process it started is terminated

---

### Requirement: Unified health surface aggregates all components
`Daemon.App` SHALL maintain a health registry of typed component statuses, each a `ComponentHealth` with a name, a status of `ok | degraded | down | unknown`, and an optional detail string. The registry SHALL include the Gateway, the Dcal server, the Dmail server, Tailscale, and each configured model-runner endpoint, and the menu SHALL render one status row per component.

#### Scenario: Menu lists every component's health
- **WHEN** the menu is opened
- **THEN** it shows one row per registered component reflecting that component's current `ComponentHealth`

#### Scenario: Health changes propagate to the menu and icon
- **WHEN** a component's status changes (e.g. a server goes from `ok` to `down`)
- **THEN** the corresponding menu row and the aggregate status icon update to reflect the new status

---

### Requirement: Dmail server health is probed
`Daemon.App` SHALL periodically probe the Dmail server's health endpoint (using `DMAIL_BASE_URL` and `DMAIL_API_KEY`) and publish the result as the Dmail component's `ComponentHealth`.

#### Scenario: Reachable Dmail server reports ok
- **WHEN** the Dmail health endpoint returns a success response
- **THEN** the Dmail component status is `ok`

#### Scenario: Unreachable Dmail server reports down
- **WHEN** the Dmail health endpoint cannot be reached or returns an error response
- **THEN** the Dmail component status is `down`

---

### Requirement: Configured model-runner endpoints are health-probed
`Daemon.App` SHALL probe each configured inference endpoint (`DMON_E2B_URL`, `DMON_REASONER_URL`, and the egress target) for reachability and publish each as a `ComponentHealth`, treating any HTTP response as reachable. It SHALL NOT special-case individual runner implementations (Ollama, Omlx, MTPLX, Llama.cpp) nor enumerate models.

#### Scenario: Reachable endpoint reports ok
- **WHEN** a configured inference endpoint returns any HTTP response
- **THEN** that endpoint's component status is `ok`

#### Scenario: Unreachable endpoint reports down
- **WHEN** a configured inference endpoint cannot be reached
- **THEN** that endpoint's component status is `down`

---

### Requirement: Menu provides a best-effort Bring Tailscale up action
The menu SHALL offer an action that runs `tailscale up` on a best-effort basis when Tailscale is not up, surfacing the outcome in the Tailscale status row. Interactive authentication/login flows are out of scope.

#### Scenario: Bring-up action invoked when Tailscale is down
- **WHEN** Tailscale is not up and the user selects the Bring Tailscale up action
- **THEN** `Daemon.App` runs `tailscale up` and reflects the resulting status in the Tailscale row
