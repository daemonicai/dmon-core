## MODIFIED Requirements

### Requirement: Top-level role buckets

The repository SHALL organise first-party projects into top-level role buckets, each holding the projects of one role: `core/` (contracts + engine: `Dmon.Abstractions`, `Dmon.Protocol`, `Dmon.Core`, `Dmon.Runtime`, `Dmon.Protocol.SchemaGen`), `providers/` (provider packages), `tools/` (tool packages), `memory/` (memory backend implementations + the `IMemory` facade), `middleware/` (ADR-023 chat-pipeline middleware packages), `frontends/` (protocol-surface host apps), `daemon/` (the Daemon personal-assistant *composition*: the `Daemon.cs` composition root, the `Daemon.Routing` triage-policy library, and the `Daemon.App` Swift menu bar app — ADR-028), `services/` (standalone backing **server** apps that pair with a `tools/` extension — e.g. the `Dcal` iCal-sync server; app artifacts, independently versioned, not on the protocol-lockstep train — ADR-028), `home/` (the macOS host product `dmon-home` — the Swift application that supervises the Mac-side stack and hosts the local voice loop as a gateway client, together with the product's requirements document; it contains no .NET projects and carries no `.slnx`, and it ships **no release artifact**, joining the app-artifact family only if and when it gains one — ADR-037), and `samples/` (composition-root examples + the prebuilt stock default core). The memory **contracts** (`Dmon.Abstractions.Memory`) remain part of `core/`; only memory **implementations** live under `memory/`. A role bucket with no current members (e.g. `middleware/` until the first `IDmonMiddleware` ships) SHALL NOT exist as a directory; the role remains defined and its bucket materialises with its first member. No first-party project SHALL remain under a flat `src/` or a top-level `extensions/` directory.

#### Scenario: Every solution project lives in a bucket

- **WHEN** the repository is inspected
- **THEN** each project referenced by any `.slnx` resolves to a path under `core/`, `providers/`, `tools/`, `memory/`, `middleware/`, `frontends/`, `daemon/`, `services/`, `samples/`, or `test/`
- **AND** neither `src/` nor `extensions/` contains any tracked first-party project

#### Scenario: No ghost or cruft project directories

- **WHEN** the repository is inspected
- **THEN** the former ghost directories (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui`) do not exist
- **AND** no project directory contains only `bin`/`obj` build artifacts with no source

#### Scenario: A memberless role bucket has no directory

- **WHEN** the repository is inspected and a role (e.g. middleware) has no member projects
- **THEN** there is no top-level directory or `.slnx` for that role
- **AND** the role remains a defined naming family for when its first member is added

#### Scenario: The Daemon composition and its backing servers are separated

- **WHEN** the `daemon/` and `services/` buckets are inspected
- **THEN** `daemon/` holds the Daemon composition (`Daemon.cs`, `Daemon.Routing`, `Daemon.App`) and not a backing server
- **AND** the `Dcal` iCal-sync server resides under `services/`, not `daemon/` or `tools/`

#### Scenario: The macOS host lives in the home bucket

- **WHEN** the `home/` bucket is inspected
- **THEN** it holds the `dmon-home` macOS host product and contains no .NET project
- **AND** no `home.slnx` exists, because the bucket has no C# members

### Requirement: Per-area solutions and a root superset

The repository SHALL provide one `.slnx` per area that has C# members (`core.slnx`, `providers.slnx`, `tools.slnx`, `memory.slnx`, `frontends.slnx`, `daemon.slnx`, `services.slnx`) and a root `Everything.slnx` that includes every **shipped** first-party project and every test project. An area with no C# member projects SHALL NOT carry a `.slnx`. Throwaway, experimental, or spike projects (e.g. a resolved scripting spike) are neither shipped first-party projects nor tests: they SHALL NOT be members of `Everything.slnx` or any area `.slnx`, and SHALL NOT be tracked in the repository once the spike is resolved. Swift packages — `daemon/Daemon.App` and the `home/` host packages — are **not** .NET projects and SHALL NOT be referenced by any `.slnx`; each is built via its own toolchain through its own `make` target. The standalone `Dmon.slnx` SHALL NOT exist.

#### Scenario: Area solution builds in isolation

- **WHEN** `dotnet build memory.slnx -c Release` is run
- **THEN** the memory projects and their dependencies build successfully without errors or warnings

#### Scenario: Everything solution builds and tests the whole repo

- **WHEN** `make build` and `make test` are run against `Everything.slnx`
- **THEN** the build is clean (no errors, `TreatWarningsAsErrors` satisfied)
- **AND** all existing test projects are discovered and pass

#### Scenario: Swift package is excluded from the .NET solutions

- **WHEN** `Everything.slnx`, the root `daemon.slnx`, and every other area `.slnx` are inspected
- **THEN** none references `daemon/Daemon.App` or any `home/` package (all Swift packages)
- **AND** each Swift package builds via its own `make` target using `swift build -c release`

#### Scenario: No throwaway spike in the superset

- **WHEN** `Everything.slnx` is inspected
- **THEN** it contains no throwaway/experimental spike project (e.g. no `spike/ScriptingSpike`)
- **AND** no such spike directory is tracked in the repository
