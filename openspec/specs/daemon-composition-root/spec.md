## Purpose

Define the standing contract for `daemon/Daemon.cs`, the Daemon personal-assistant composition root (ADR-028): how it wires triage routing over three backends (e2b local, local reasoner, gated cloud egress), which personal-scope abilities it registers (dcal, Dmail, memory), and the requirement that all credentials and model endpoints come from the environment or `~/.dmon/config.yaml` rather than source literals.

## Requirements

### Requirement: Daemon.cs wires triage routing with three backends
`daemon/Daemon.cs` SHALL register `UseTriage` (e2b local), `AddReasoner` (26B local), and `AddEgress` (gated cloud egress, supplied as an explicit `IChatClient`) so that `TriageRouter` is the terminal `IChatClient` when the host is built.

#### Scenario: TriageRouter is the terminal client
- **WHEN** `Daemon.cs` is built and run as a composition root
- **THEN** the terminal `IChatClient` is a `TriageRouter` instance (verified via integration test)

#### Scenario: Egress client not called on personal-scope turns
- **WHEN** a turn classifies as scope `"personal"`
- **THEN** no HTTP request is made to the egress (cloud) endpoint

---

### Requirement: Daemon.cs registers all personal-scope abilities
`daemon/Daemon.cs` SHALL call `AddDcalAbilities()`, `AddToolExtension<DmailExtension>()`, and `AddDmonMemory()` so that personal-scope turns have access to calendar (dcal), email, and memory tools.

#### Scenario: Calendar, Dmail, and memory tools available on personal turns
- **WHEN** a turn is classified as scope `"personal"` and dispatched
- **THEN** `ChatOptions.Tools` contains `lookup_calendar`, `list_upcoming_events`, `search_email`, `check_new_messages`, and memory tools

#### Scenario: Personal abilities absent from world-scope turns
- **WHEN** a turn is classified as scope `"world"`
- **THEN** `ChatOptions.Tools` does NOT contain `lookup_calendar`, `search_email`, or memory tools

---

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

---

### Requirement: ADR-028 resolves ADR-025 Open Question B — daemon/ and services/ buckets, dcal rename, and Swift
An ADR (ADR-028, amending ADR-025 D2/D10/D11) SHALL be written that: (a) folds `daemon/` into the monorepo as the Daemon composition bucket (superseding ADR-025 D11's "keep separate" for `daemon`); (b) adds a `services/` bucket for standalone backing servers that pair with a `tools/` extension; (c) establishes `daemon/Daemon.App` as the `dmonium` macOS app artifact; (d) renames the calendar capability to `dcal` (server `services/Dcal`, tool `tools/Dmon.Tools.Dcal`, specs `dcal-lookup`/`dcal-sync`); (e) records that Swift Packages in `daemon/` are built outside `Everything.slnx` via `make daemon-app`.

#### Scenario: ADR accepted before implementation
- **WHEN** the daemon-app change tasks are started
- **THEN** ADR-028 exists in `docs/adrs/`, has status Accepted, and is referenced from the change
