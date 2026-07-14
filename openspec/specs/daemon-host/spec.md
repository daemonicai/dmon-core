## Purpose

Define the standing contract for `daemon/Daemon.App`, the `dmonium` macOS host (ADR-028): a window as the primary surface with an aggregate live status rollup applied to the Dock icon and window (and an optional, default-off menu-bar icon), Gateway process lifecycle management (launch, crash-restart, quit-cleanup) driven window-independently, an in-window settings section that manages all Daemon configuration with Keychain-backed secrets, login-item registration via `SMAppService`, periodic Tailscale status polling, and per-service / Tailscale control actions.
## Requirements
### Requirement: Window application shows live aggregate status
`Daemon.App` SHALL present a window as its primary surface, with a Dock icon and Cmd-Tab presence (regular activation policy). It SHALL reflect an aggregate rollup of all supervised and monitored components (the Network host, Dcal server, Dmail server, Tailscale, and the configured model-runner endpoints) as a colour state, and SHALL apply that colour to the Dock icon and the window's status surface. Rollup: if the Network host is stopped, the state is red; otherwise if any component is `down`, red; otherwise if any component is `degraded` or `unknown`, amber; otherwise green. A menu-bar status icon, driven by the same rollup, SHALL be available as an optional surface controlled by a persisted setting that is **off by default**.

#### Scenario: All components healthy shows green state
- **WHEN** the Network host is running, Tailscale is up, and every other monitored component reports `ok`
- **THEN** the Dock icon and window status surface are in the green state

#### Scenario: Network host stopped shows red state
- **WHEN** the Network host process has exited or was never started
- **THEN** the Dock icon and window status surface are in the red state

#### Scenario: A monitored dependency down shows red state
- **WHEN** the Network host is running but a monitored component (e.g. the Dmail server or a configured model-runner endpoint) reports `down`
- **THEN** the Dock icon and window status surface are in the red state

#### Scenario: Tailscale degraded shows amber state
- **WHEN** the Network host is running and no component is `down`, but Tailscale reports `degraded` or a component is `unknown` (not configured)
- **THEN** the Dock icon and window status surface are in the amber state

#### Scenario: Menu-bar icon is off by default and reflects the rollup when enabled
- **WHEN** the app launches with no prior preference, and then the user enables the show-menu-bar-icon setting
- **THEN** no menu-bar icon is shown initially, and once enabled a menu-bar icon appears reflecting the same rollup colour as the Dock icon

### Requirement: Settings section manages all Daemon configuration
`Daemon.App` SHALL present configuration as a section within its primary window (not a separate settings scene), reachable via the window's sidebar and the standard settings keyboard shortcut. The settings section SHALL expose fields for: the Gemini API key; the Dmail base URL and API key; the Calendar iCal URL and API key; the egress model ID (`DMON_EGRESS_MODEL`); the egress confidence threshold (`DMON_EGRESS_THRESHOLD`); the calendar sync interval; and the Dcal/Dmail server binary paths (`DMON_DCAL_SERVER_PATH`, `DMON_DMAIL_SERVER_PATH`). It SHALL NOT expose local-inference endpoint or model-ID fields (the former `DMON_E2B_URL`/`DMON_REASONER_URL`/`DMON_E2B_MODEL`/`DMON_REASONER_MODEL`): the local mlx runtimes are provider-spawned on fixed ports with baked model ids and are not configured through dmonium. On save, secrets are stored in Keychain, non-secret values are written to `~/.dmon/config.yaml`, and the Network host is signalled to restart so new settings take effect. Settings for dmon-core itself use the `DMON_` env prefix; `DCAL_*`/`DMAIL_*` server vars and provider keys are unchanged.

#### Scenario: Settings reachable from the window
- **WHEN** the user opens the Settings section from the window sidebar or presses the settings keyboard shortcut
- **THEN** the configuration fields are shown within the primary window, not in a separate window

#### Scenario: API keys stored in Keychain
- **WHEN** an API key is saved in the settings section
- **THEN** it is stored in the macOS Keychain (not plaintext in config.yaml)

#### Scenario: Non-secret settings written to config.yaml
- **WHEN** a non-secret setting (egress model ID, egress threshold, server path, sync interval) is saved
- **THEN** it is written to `~/.dmon/config.yaml` under its `DMON_`-prefixed key

#### Scenario: Egress model ID is configurable without editing source
- **WHEN** the egress model ID field (`DMON_EGRESS_MODEL`) is saved and the Network host restarts the core
- **THEN** the restarted core uses the configured egress model ID, with no edit to `Daemon.cs`

#### Scenario: No local-inference endpoint or model fields are shown
- **WHEN** the user opens the Settings section
- **THEN** there are no fields for a local-inference endpoint URL or local model ID (no E2B/Reasoner endpoint or model fields)

#### Scenario: Save triggers core session restart
- **WHEN** settings are saved
- **THEN** the Network host is signalled to restart the active core session so new settings take effect

### Requirement: Daemon.App registers as a macOS login item
On first launch, `Daemon.App` SHALL register itself as a login item via `SMAppService` so the Daemon starts automatically at login.

#### Scenario: Login item registered on first launch
- **WHEN** `Daemon.App` is launched for the first time
- **THEN** it is registered as a login item and will launch at next login

#### Scenario: Login item can be toggled in settings
- **WHEN** the user disables the login item toggle in settings
- **THEN** `SMAppService` unregisters the app from login items

---

### Requirement: Tailscale status polled every 30 seconds
`TailscaleMonitor` SHALL execute `tailscale status --json` every 30 seconds and update the aggregate status surface (Dock icon, window status, and the optional menu-bar icon when enabled) accordingly.

#### Scenario: Tailscale up reflected in status within 30 seconds
- **WHEN** Tailscale comes up after being down
- **THEN** the status icon updates to running/degraded within 30 seconds of the next poll

#### Scenario: Missing Tailscale CLI treated as down
- **WHEN** the `tailscale` binary is not found in PATH
- **THEN** the status is treated as degraded (amber) and a tooltip notes Tailscale is not installed

---

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
`Daemon.App` SHALL maintain a health registry of typed component statuses, each a `ComponentHealth` with a name, a status of `ok | degraded | down | unknown`, an optional detail string, and a last-updated timestamp recording when the component last published a status. The registry SHALL include the Network host, the Dcal server, the Dmail server, Tailscale, and each configured model-runner endpoint, and the window dashboard SHALL render one status row per component, showing the component's status and its last-updated time.

#### Scenario: Dashboard lists every component's health
- **WHEN** the dashboard Status section is shown
- **THEN** it shows one row per registered component reflecting that component's current `ComponentHealth`, including a last-updated time

#### Scenario: Health changes propagate to the dashboard and rollup
- **WHEN** a component's status changes (e.g. a server goes from `ok` to `down`)
- **THEN** the corresponding dashboard row, its last-updated time, and the aggregate rollup state update to reflect the new status

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
`Daemon.App` SHALL probe the configured egress endpoint for reachability and publish it as a `ComponentHealth`, treating any HTTP response as reachable. It SHALL NOT health-probe the local mlx inference runtimes (first-line / escalation): those are provider-spawned by `Dmon.Providers.Mlx`, which owns their lifecycle and readiness, so dmonium does not duplicate that health. It SHALL NOT special-case individual runner implementations nor enumerate models.

#### Scenario: Reachable egress endpoint reports ok
- **WHEN** the configured egress endpoint returns any HTTP response
- **THEN** that endpoint's component status is `ok`

#### Scenario: Unreachable egress endpoint reports down
- **WHEN** the configured egress endpoint cannot be reached
- **THEN** that endpoint's component status is `down`

#### Scenario: No local-inference health rows
- **WHEN** the health surface is published
- **THEN** it contains no "E2B Endpoint" or "Reasoner Endpoint" component (dmonium does not probe local inference runtimes)

---

### Requirement: Window provides component and Tailscale control actions
The window dashboard SHALL offer, per supervised service (the Network host, Dcal server, Dmail server), start / stop / restart actions, and SHALL offer a best-effort Bring-Tailscale-up action that runs `tailscale up` when Tailscale is not up, surfacing the outcome in the Tailscale status row. Interactive authentication/login flows are out of scope.

#### Scenario: Restart a supervised service from the dashboard
- **WHEN** the user selects Restart for a running supervised service in the dashboard Services section
- **THEN** `Daemon.App` stops and relaunches that service's process

#### Scenario: Bring-up action invoked when Tailscale is down
- **WHEN** Tailscale is not up and the user selects the Bring Tailscale up action in the dashboard
- **THEN** `Daemon.App` runs `tailscale up` and reflects the resulting status in the Tailscale row

### Requirement: Network host process is started on app launch and restarted on crash
`NetworkManager` SHALL launch the Network host process when `Daemon.App` starts. Supervision startup SHALL be window-independent — driven once at application launch (before or without any window being shown), not by the appearance of a view — so the Network host and other supervised processes start even when the app is launched as a login item with no visible window. If the process exits unexpectedly, it SHALL restart it with exponential back-off (initial delay 2s, max 60s). Closing the application window SHALL NOT stop supervision or terminate the Network host.

#### Scenario: Network host starts on app launch
- **WHEN** `Daemon.App` launches and no Network host process is running
- **THEN** the Network host process is started within 2 seconds

#### Scenario: Network host starts on headless login-item launch
- **WHEN** `Daemon.App` is launched as a login item and no application window is shown
- **THEN** the Network host process is still started

#### Scenario: Crashed Network host is restarted
- **WHEN** the Network host process exits with a non-zero status
- **THEN** `NetworkManager` restarts it after a back-off delay

#### Scenario: Closing the window does not stop the Network host
- **WHEN** the user closes the application window while the Network host is running
- **THEN** the Network host child process keeps running and the app continues supervising it

#### Scenario: Network host process terminates when app quits
- **WHEN** `Daemon.App` quits
- **THEN** the Network host child process is terminated

