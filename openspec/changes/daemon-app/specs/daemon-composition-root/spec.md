## ADDED Requirements

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
`daemon/Daemon.cs` SHALL call `AddCalendarAbilities()`, `AddToolExtension<DmailExtension>()`, and `AddDmonMemory()` so that personal-scope turns have access to calendar, email, and memory tools.

#### Scenario: Calendar, Dmail, and memory tools available on personal turns
- **WHEN** a turn is classified as scope `"personal"` and dispatched
- **THEN** `ChatOptions.Tools` contains `lookup_calendar`, `list_upcoming_events`, `search_email`, `check_new_messages`, and memory tools

#### Scenario: Personal abilities absent from world-scope turns
- **WHEN** a turn is classified as scope `"world"`
- **THEN** `ChatOptions.Tools` does NOT contain `lookup_calendar`, `search_email`, or memory tools

---

### Requirement: Daemon.cs reads credentials from environment and config, not hardcoded
All API keys and model endpoints in `daemon/Daemon.cs` SHALL be sourced from environment variables or `~/.dmon/config.yaml`. No credential values SHALL appear as literals in the source file.

#### Scenario: Egress key from environment
- **WHEN** `GEMINI_API_KEY` is set in the environment
- **THEN** the egress `IChatClient` passed to `AddEgress(...)` is constructed with that key, with no key literal in `Daemon.cs`

#### Scenario: Model endpoint from config
- **WHEN** the e2b or reasoner endpoint is set in `~/.dmon/config.yaml`
- **THEN** `Daemon.cs` uses the configured endpoint instead of the default

---

### Requirement: ADR-025 Open Question B resolved — daemon/ bucket and Swift in monorepo documented
An ADR amendment SHALL be written that: (a) folds `daemon/` into the monorepo (superseding ADR-025 D11's "keep separate" for `daemon`); (b) establishes `daemon/Daemon.App` as the `dmonium` macOS app artifact; (c) records that Swift Packages in `daemon/` are built outside `Everything.slnx` via `make daemon-app`.

#### Scenario: ADR amendment accepted before implementation
- **WHEN** the daemon-app change tasks are started
- **THEN** the ADR amendment exists in `docs/adrs/` and is referenced from the change
