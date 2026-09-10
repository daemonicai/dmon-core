## REMOVED Requirements

### Requirement: Session discovery — project-local by default
**Reason**: Contradicted the implemented and ADR-004-specified root marker. It named a bare `.daemon/` directory (pre-rename naming) as the marker, while ADR-004's resolution algorithm and the code use `.dmon/config.yaml`. Its bootstrap scenario described creating `.daemon/` at CWD, but the core bootstraps `~/.dmon/`. Its scenario titles carried the stale naming, so it could not be corrected in place.
**Migration**: Replaced by *Session discovery — `.dmon/config.yaml` marks the project root* below. No runtime behaviour changes, and no session moves.

## ADDED Requirements

### Requirement: Session discovery — `.dmon/config.yaml` marks the project root
The system SHALL discover the session store by walking up the directory tree from CWD, looking for a `.dmon/config.yaml` **file**. The nearest directory containing one is the project root. When a root is found, sessions SHALL be stored in that root's `.dmon/sessions/` unless its configuration redirects them. When no root is found, sessions SHALL be stored in `~/.dmon/sessions/`. A `.dmon/` directory that does not contain `config.yaml` (for example, one holding only the app-managed `config.local.yaml`) SHALL NOT mark a project root.

#### Scenario: Project-local store used when .dmon/config.yaml exists
- **WHEN** the agent is invoked from a directory with a `.dmon/config.yaml` in its ancestor tree
- **THEN** sessions are stored in the `.dmon/sessions/` directory beside the nearest such `config.yaml`

#### Scenario: Global store used when no .dmon/config.yaml found
- **WHEN** no `.dmon/config.yaml` exists in the ancestor tree
- **THEN** sessions are stored in `~/.dmon/sessions/`

#### Scenario: A .dmon/ directory without config.yaml is not a root
- **WHEN** the agent is invoked from a directory whose `.dmon/` contains `config.local.yaml` but no `config.yaml`, and no ancestor contains `.dmon/config.yaml`
- **THEN** sessions are stored in `~/.dmon/sessions/`, not in that directory's `.dmon/sessions/`

#### Scenario: First-use bootstrap creates ~/.dmon/
- **WHEN** the core starts and neither `~/.dmon/config.yaml` nor any `.dmon/config.yaml` in the CWD's ancestor tree exists
- **THEN** the core creates `~/.dmon/` with a default `config.yaml` and an empty `sessions/`, then emits `bootstrapNotice {path, created[]}` listing what it created before continuing

#### Scenario: Store redirected to global via config
- **WHEN** the project root's `.dmon/config.yaml` contains `sessionStore: global`
- **THEN** sessions are stored in `~/.dmon/sessions/` even though a project root was found
