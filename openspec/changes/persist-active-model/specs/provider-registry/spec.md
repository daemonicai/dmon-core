## ADDED Requirements

### Requirement: Active provider and model selection is persisted

The system SHALL persist the active provider name and active model id so the selection survives a restart. Persistence SHALL use a dedicated `state.yaml` file resolved to **project scope** (`<cwd>/.dmon/state.yaml`) when a project `.dmon` directory exists, otherwise **global scope** (`~/.dmon/state.yaml`). Writes SHALL be atomic (temp file then move) and SHALL NOT require an external YAML library.

At startup, `ProviderRegistry` SHALL initialise its active provider index and active model id from the persisted selection when the persisted provider is currently configured. When no selection is persisted, the file is absent or unreadable, or the persisted provider is not configured, the registry SHALL fall back to the default (first configured provider, index 0) without throwing.

When a provider/model switch is committed, the new active provider name and model id SHALL be written to the store.

#### Scenario: Selection saved when a switch is committed

- **WHEN** a pending provider/model switch is committed
- **THEN** the active provider name and model id are written to `state.yaml` (project scope if a project `.dmon` exists, else global)

#### Scenario: Selection restored at startup

- **WHEN** the agent core starts and `state.yaml` records an active provider that is currently configured
- **THEN** `ProviderRegistry` makes that provider active and sets `GetCurrentModelId()` to the persisted model id, instead of defaulting to the first configured provider

#### Scenario: Project scope overrides global

- **WHEN** both `<cwd>/.dmon/state.yaml` and `~/.dmon/state.yaml` exist and a project `.dmon` directory is present
- **THEN** the project-scope `state.yaml` is used for both load and save

#### Scenario: Absent or stale selection falls back to default

- **WHEN** no `state.yaml` exists, or it is unreadable, or the persisted provider is no longer configured
- **THEN** the registry uses the default first configured provider (index 0) and does not throw
