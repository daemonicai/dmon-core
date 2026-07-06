## MODIFIED Requirements

### Requirement: Settings section manages all Daemon configuration
`Daemon.App` SHALL present configuration as a section within its primary window (not a separate settings scene), reachable via the window's sidebar and the standard settings keyboard shortcut. The settings section SHALL expose fields for: the Gemini API key; the Dmail base URL and API key; the Calendar iCal URL and API key; the egress model ID (`DMON_EGRESS_MODEL`); the egress confidence threshold (`DMON_EGRESS_THRESHOLD`); the calendar sync interval; and the Dcal/Dmail server binary paths (`DMON_DCAL_SERVER_PATH`, `DMON_DMAIL_SERVER_PATH`). It SHALL NOT expose local-inference endpoint or model-ID fields (the former `DMON_E2B_URL`/`DMON_REASONER_URL`/`DMON_E2B_MODEL`/`DMON_REASONER_MODEL`): the local mlx runtimes are provider-spawned on fixed ports with baked model ids and are not configured through dmonium. On save, secrets are stored in Keychain, non-secret values are written to `~/.dmon/config.yaml`, and the Gateway is signalled to restart so new settings take effect. Settings for dmon-core itself use the `DMON_` env prefix; `DCAL_*`/`DMAIL_*` server vars and provider keys are unchanged.

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
- **WHEN** the egress model ID field (`DMON_EGRESS_MODEL`) is saved and the Gateway restarts the core
- **THEN** the restarted core uses the configured egress model ID, with no edit to `Daemon.cs`

#### Scenario: No local-inference endpoint or model fields are shown
- **WHEN** the user opens the Settings section
- **THEN** there are no fields for a local-inference endpoint URL or local model ID (no E2B/Reasoner endpoint or model fields)

#### Scenario: Save triggers core session restart
- **WHEN** settings are saved
- **THEN** the Gateway is signalled to restart the active core session so new settings take effect

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
