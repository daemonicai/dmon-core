## MODIFIED Requirements

### Requirement: Per-area solutions and a root superset

The repository SHALL provide one `.slnx` per area that has C# members (`core.slnx`, `providers.slnx`, `tools.slnx`, `memory.slnx`, `frontends.slnx`, `daemon.slnx`, `services.slnx`) and a root `Everything.slnx` that includes every **shipped** first-party project and every test project. An area with no C# member projects SHALL NOT carry a `.slnx`. Throwaway, experimental, or spike projects (e.g. a resolved scripting spike) are neither shipped first-party projects nor tests: they SHALL NOT be members of `Everything.slnx` or any area `.slnx`, and SHALL NOT be tracked in the repository once the spike is resolved. Swift packages (e.g. `daemon/Daemon.App`) are **not** .NET projects and SHALL NOT be referenced by any `.slnx`; they are built via their own toolchain (`make daemon-app`). The standalone `Dmon.slnx` SHALL NOT exist.

#### Scenario: Area solution builds in isolation

- **WHEN** `dotnet build memory.slnx -c Release` is run
- **THEN** the memory projects and their dependencies build successfully without errors or warnings

#### Scenario: Everything solution builds and tests the whole repo

- **WHEN** `make build` and `make test` are run against `Everything.slnx`
- **THEN** the build is clean (no errors, `TreatWarningsAsErrors` satisfied)
- **AND** all existing test projects are discovered and pass

#### Scenario: Swift package is excluded from the .NET solutions

- **WHEN** `Everything.slnx` and the root `daemon.slnx` are inspected
- **THEN** neither references `daemon/Daemon.App` (a Swift package)
- **AND** `make daemon-app` builds it via `swift build -c release`

#### Scenario: No throwaway spike in the superset

- **WHEN** `Everything.slnx` is inspected
- **THEN** it contains no throwaway/experimental spike project (e.g. no `spike/ScriptingSpike`)
- **AND** no such spike directory is tracked in the repository
