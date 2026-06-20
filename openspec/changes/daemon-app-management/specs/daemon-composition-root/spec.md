## MODIFIED Requirements

### Requirement: Daemon.cs reads credentials from environment and config, not hardcoded
All API keys, model endpoints, and model IDs in `daemon/Daemon.cs` SHALL be sourced from environment variables or `~/.dmon/config.yaml` via the host `IConfiguration` (read through `builder.Configuration`), with built-in defaults. No credential values, endpoint literals, or model-ID literals (beyond defaults) SHALL be required in source. dmon-core's own inference settings SHALL use the `DMON_` env prefix: `DMON_E2B_URL`, `DMON_REASONER_URL`, `DMON_E2B_MODEL`, `DMON_REASONER_MODEL`, `DMON_EGRESS_MODEL`. The Gemini key (`GEMINI_API_KEY`) and the `DCAL_*`/`DMAIL_*` server vars are unchanged.

#### Scenario: Egress key from environment
- **WHEN** `GEMINI_API_KEY` is set in the environment
- **THEN** the egress `IChatClient` passed to `AddEgress(...)` is constructed with that key, with no key literal in `Daemon.cs`

#### Scenario: Model endpoint from config
- **WHEN** `DMON_E2B_URL` or `DMON_REASONER_URL` is set in `~/.dmon/config.yaml` or the environment
- **THEN** `Daemon.cs` reads it via `builder.Configuration` and uses the configured endpoint instead of the default

#### Scenario: Model ID from config
- **WHEN** `DMON_E2B_MODEL`, `DMON_REASONER_MODEL`, or `DMON_EGRESS_MODEL` is set in `~/.dmon/config.yaml` or the environment
- **THEN** the corresponding backend `IChatClient` is constructed with the configured model ID, with no model-ID literal required in `Daemon.cs`

#### Scenario: Defaults apply when unset
- **WHEN** a `DMON_` inference key is absent from both environment and config
- **THEN** `Daemon.cs` uses its built-in default value for that endpoint or model ID
