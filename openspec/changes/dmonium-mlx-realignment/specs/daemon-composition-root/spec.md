## MODIFIED Requirements

### Requirement: Daemon.cs wires triage routing with three backends
`daemon/Daemon.cs` SHALL register the two keyed mlx runtimes (`AddMlxFirstline()`, `AddMlxEscalation()`) and wire triage routing over three backends: `UseTriage` (first-line mlx runtime, resolved via `MlxClient(MlxRuntimeKeys.Firstline)`), `AddEscalation` (escalation mlx runtime, resolved via `MlxClient(MlxRuntimeKeys.Escalation)`), and `AddEgress` (gated cloud egress, supplied as an explicit `IChatClient`), so that `TriageRouter` is the terminal `IChatClient` when the host is built. It SHALL also register `AddEscalationWarming` against the escalation runtime so the larger local model is warmed on activity and idle-released. There is no `AddReasoner`/upfront-tier registration — the larger local model is reached only by first-line `think_harder` escalation.

#### Scenario: TriageRouter is the terminal client
- **WHEN** `Daemon.cs` is built and run as a composition root
- **THEN** the terminal `IChatClient` is a `TriageRouter` instance (verified via integration test)

#### Scenario: Egress client not called on personal-scope turns
- **WHEN** a turn classifies as scope `"personal"`
- **THEN** no HTTP request is made to the egress (cloud) endpoint

#### Scenario: Escalation warming is registered
- **WHEN** `Daemon.cs` is built
- **THEN** an `EscalationWarmingService` is registered as an `ISessionActivityListener` against the escalation mlx runtime

---

### Requirement: Daemon.cs reads credentials from environment and config, not hardcoded
`daemon/Daemon.cs` SHALL source its API keys and egress model ID from environment variables or `~/.dmon/config.yaml` via the host `IConfiguration` (read through `builder.Configuration`), with built-in defaults. No credential values or model-ID literals (beyond defaults) SHALL be required in source. The only `DMON_`-prefixed inference setting read by `Daemon.cs` is `DMON_EGRESS_MODEL` (egress model, default `gemini-3.1-flash-lite`); the Gemini key is `GEMINI_API_KEY`. The local mlx first-line and escalation runtimes are NOT configured through `Daemon.cs` environment variables: they are registered via `AddMlxFirstline()`/`AddMlxEscalation()` and use `Dmon.Providers.Mlx`'s baked fixed-port and verified-quant defaults. The `DCAL_*`/`DMAIL_*` server vars are unchanged.

#### Scenario: Egress key from environment
- **WHEN** `GEMINI_API_KEY` is set in the environment
- **THEN** the egress `IChatClient` passed to `AddEgress(...)` is constructed with that key, with no key literal in `Daemon.cs`

#### Scenario: Egress model ID from config
- **WHEN** `DMON_EGRESS_MODEL` is set in `~/.dmon/config.yaml` or the environment
- **THEN** the egress `IChatClient` is constructed with the configured model ID, with no model-ID literal required in `Daemon.cs`

#### Scenario: Local mlx runtimes need no endpoint or model configuration
- **WHEN** `Daemon.cs` is built with no `DMON_E2B_*`/`DMON_REASONER_*` variables set
- **THEN** the first-line and escalation mlx runtimes are registered via `AddMlxFirstline()`/`AddMlxEscalation()` on their provider-default fixed ports with their baked model ids, and `Daemon.cs` reads no local-inference endpoint or model-ID variable

#### Scenario: Egress defaults apply when unset
- **WHEN** `DMON_EGRESS_MODEL` is absent from both environment and config
- **THEN** `Daemon.cs` uses its built-in default egress model ID (`gemini-3.1-flash-lite`)
