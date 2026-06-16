## MODIFIED Requirements

### Requirement: Top-level role buckets

The repository SHALL organise first-party projects into top-level role buckets, each holding the projects of one role: `core/` (contracts + engine: `Dmon.Abstractions`, `Dmon.Protocol`, `Dmon.Core`, `Dmon.Runtime`, `Dmon.Protocol.SchemaGen`), `providers/` (provider packages), `tools/` (tool packages), `memory/` (memory backend implementations + the `IMemory` facade), `middleware/` (ADR-023 chat-pipeline middleware packages), `frontends/` (host apps), and `samples/` (composition-root examples + the prebuilt stock default core). The memory **contracts** (`Dmon.Abstractions.Memory`) remain part of `core/`; only memory **implementations** live under `memory/`. A role bucket with no current members (e.g. `middleware/` until the first `IDmonMiddleware` ships) SHALL NOT exist as a directory; the role remains defined and its bucket materialises with its first member. No first-party project SHALL remain under a flat `src/` or a top-level `extensions/` directory.

#### Scenario: Every solution project lives in a bucket

- **WHEN** the repository is inspected
- **THEN** each project referenced by any `.slnx` resolves to a path under `core/`, `providers/`, `tools/`, `memory/`, `middleware/`, `frontends/`, `samples/`, or `test/`
- **AND** neither `src/` nor `extensions/` contains any tracked first-party project

#### Scenario: No ghost or cruft project directories

- **WHEN** the repository is inspected
- **THEN** the former ghost directories (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui`) do not exist
- **AND** no project directory contains only `bin`/`obj` build artifacts with no source

#### Scenario: A memberless role bucket has no directory

- **WHEN** the repository is inspected and a role (e.g. middleware) has no member projects
- **THEN** there is no top-level directory or `.slnx` for that role
- **AND** the role remains a defined naming family for when its first member is added

### Requirement: Per-area solutions and a root superset

The repository SHALL provide one `.slnx` per area that has members (`core.slnx`, `providers.slnx`, `tools.slnx`, `memory.slnx`, `frontends.slnx`) and a root `Everything.slnx` that includes every first-party and test project. An area with no member projects SHALL NOT carry a `.slnx`. The standalone `Dmon.slnx` SHALL NOT exist.

#### Scenario: Area solution builds in isolation

- **WHEN** `dotnet build memory.slnx -c Release` is run
- **THEN** the memory projects and their dependencies build successfully without errors or warnings

#### Scenario: Everything solution builds and tests the whole repo

- **WHEN** `make build` and `make test` are run against `Everything.slnx`
- **THEN** the build is clean (no errors, `TreatWarningsAsErrors` satisfied)
- **AND** all existing test projects are discovered and pass

### Requirement: Package naming families

First-party packageable projects SHALL follow the ADR-023 D3 naming families: providers as `Dmon.Providers.<Name>`, tools as `Dmon.Tools.<Name>`, memory backends as `Dmon.Memory.<Name>` (with the local short-term tier as the bare `Dmon.Memory`), and chat-pipeline middleware as `Dmon.Middleware.<Name>`. The Omlx provider SHALL be named `Dmon.Providers.Omlx` (assembly, namespace, and `PackageId`) and SHALL be packable.

#### Scenario: Omlx conforms to the provider family

- **WHEN** the Omlx provider project is inspected
- **THEN** its `PackageId`, assembly name, and root namespace are `Dmon.Providers.Omlx`
- **AND** it resides under `providers/`
- **AND** it is marked packable

#### Scenario: Memory backends conform to the memory family

- **WHEN** the memory projects are inspected
- **THEN** the local short-term tier is `Dmon.Memory` and the Meko long-term backend is `Dmon.Memory.Meko` (assembly, namespace, and `PackageId`)
- **AND** both reside under `memory/`
- **AND** both are marked packable
