## Purpose

Define the standing contract for `daemon/Daemon.App`, the `dmonium` macOS menu bar host (ADR-028): the live Gateway status icon, Gateway process lifecycle management (launch, crash-restart, quit-cleanup), the settings panel that manages all Daemon configuration with Keychain-backed secrets, login-item registration via `SMAppService`, and periodic Tailscale status polling.

## Requirements

### Requirement: Menu bar application shows live Gateway status
`Daemon.App` SHALL display a status icon in the macOS menu bar reflecting the current state of the Gateway process. States: running (green), degraded (amber — up but Tailscale unreachable), stopped (red).

#### Scenario: Gateway running shows green icon
- **WHEN** the Gateway process is running and Tailscale is up
- **THEN** the menu bar icon is in the green/running state

#### Scenario: Gateway stopped shows red icon
- **WHEN** the Gateway process has exited or was never started
- **THEN** the menu bar icon is in the red/stopped state

#### Scenario: Tailscale down shows amber icon
- **WHEN** the Gateway process is running but `tailscale status` reports the interface is down
- **THEN** the menu bar icon is in the amber/degraded state

---

### Requirement: Gateway process is started on app launch and restarted on crash
`GatewayManager` SHALL launch the Gateway process when `Daemon.App` starts. If the process exits unexpectedly, it SHALL restart it with exponential back-off (initial delay 2s, max 60s).

#### Scenario: Gateway starts on app launch
- **WHEN** `Daemon.App` launches and no Gateway process is running
- **THEN** the Gateway process is started within 2 seconds

#### Scenario: Crashed Gateway is restarted
- **WHEN** the Gateway process exits with a non-zero status
- **THEN** `GatewayManager` restarts it after a back-off delay

#### Scenario: Gateway process terminates when app quits
- **WHEN** `Daemon.App` quits
- **THEN** the Gateway child process is terminated

---

### Requirement: Settings panel manages all Daemon configuration
The settings panel SHALL expose fields for: Gemini API key, Dmail base URL and API key, Calendar iCal URL and API key, e2b model endpoint, reasoner model endpoint, triage confidence threshold, and calendar sync interval. On save, values are persisted and the Gateway is signalled to restart the core session.

#### Scenario: API keys stored in Keychain
- **WHEN** an API key is saved in the settings panel
- **THEN** it is stored in the macOS Keychain (not plaintext in config.yaml)

#### Scenario: Non-secret settings written to config.yaml
- **WHEN** a non-secret setting (endpoint URL, sync interval) is saved
- **THEN** it is written to `~/.dmon/config.yaml`

#### Scenario: Save triggers core session restart
- **WHEN** settings are saved
- **THEN** the Gateway is signalled to restart the active core session so new settings take effect

---

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
`TailscaleMonitor` SHALL execute `tailscale status --json` every 30 seconds and update the menu bar status icon accordingly.

#### Scenario: Tailscale up reflected in status within 30 seconds
- **WHEN** Tailscale comes up after being down
- **THEN** the status icon updates to running/degraded within 30 seconds of the next poll

#### Scenario: Missing Tailscale CLI treated as down
- **WHEN** the `tailscale` binary is not found in PATH
- **THEN** the status is treated as degraded (amber) and a tooltip notes Tailscale is not installed
