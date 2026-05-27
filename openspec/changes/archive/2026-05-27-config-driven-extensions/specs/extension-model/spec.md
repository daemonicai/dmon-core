## ADDED Requirements

### Requirement: Extensions are declared in config at user and project scope
`config.yaml` SHALL support an `extensions` list at both the project scope (`./.dmon/config.yaml`) and the user scope (`~/.dmon/config.yaml`). Each entry SHALL carry a `source` (a `nuget:` id, an assembly path, or a `.csx` path) and MAY carry optional per-entry settings.

#### Scenario: Project and user extensions are both recognized
- **WHEN** the project `config.yaml` lists source A and the user `config.yaml` lists source B
- **THEN** both A and B are part of the effective extension set

#### Scenario: Absent extensions key yields an empty set
- **WHEN** neither config file contains an `extensions` key
- **THEN** the effective extension set is empty and startup proceeds normally

### Requirement: Effective extension set is the deduplicated union of both scopes
At startup the daemon SHALL compute the effective extension set as the union of the user and project `extensions` lists, deduplicated by normalized source. The union SHALL be computed by reading both files explicitly, not via configuration array layering. Where the same source appears at both scopes, the project entry's per-entry settings SHALL win.

#### Scenario: Same source in both scopes loads once
- **WHEN** the same normalized source appears in both the user and project lists
- **THEN** the extension is loaded exactly once
- **AND** the project entry's per-entry settings are used

#### Scenario: Load order is deterministic
- **WHEN** the effective set is loaded
- **THEN** user entries are loaded before project entries, each in file order

### Requirement: Config-declared extensions load at startup without prompting
Extensions present in config SHALL be loaded at `dmoncore` startup without an interactive permission prompt. Config presence is the prior approval (the concrete meaning of an extension source approved at project/user scope). The permission/security gate SHALL instead apply when a source is added to config.

#### Scenario: Startup loads config extensions silently
- **WHEN** `dmoncore` starts with a non-empty effective extension set
- **THEN** each extension is loaded and its tools registered
- **AND** no interactive load confirmation is requested for those entries

### Requirement: There is no ephemeral runtime-load tier
The daemon SHALL NOT support loading an extension into the running process without it being present in config. Activating a source SHALL require writing it to a config scope and reloading; removing a source SHALL require deleting it from config. A core operation MAY append a source to a chosen scope's `extensions` list (running the add-time gate before writing), but SHALL report that a reload is required rather than loading into the running process.

#### Scenario: Adding a source writes config and requires reload
- **WHEN** an extension source is added via the core add operation
- **THEN** the source is written to the chosen config scope's `extensions` list
- **AND** the response indicates a reload is required to activate it

#### Scenario: Removing a source from config deactivates it on next reload
- **WHEN** a source is removed from config and the core is reloaded
- **THEN** that extension is not part of the effective set and its tools are not registered

### Requirement: A failing config extension does not abort startup
If an extension declared in config fails to load, the daemon SHALL log the failure for that entry and continue loading the remaining extensions and starting normally.

#### Scenario: One bad entry is skipped
- **WHEN** one config-declared extension fails to load and others succeed
- **THEN** the failing extension is skipped with a logged error
- **AND** the remaining extensions load and the daemon starts
