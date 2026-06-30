## Purpose

Define the standing structural contract for the dmon monorepo (ADR-025): the top-level role buckets and what each holds, the per-area `.slnx` + root `Everything.slnx` requirement, the intra-repo `ProjectReference` rule (and the `samples/` consumer-simulation exemption), the nested `Directory.Build.props` / central `Directory.Packages.props` arrangement, and the ADR-023 D3 package naming families. Future changes (satellite grafts, new packages) must conform to it.

## Requirements

### Requirement: Top-level role buckets

The repository SHALL organise first-party projects into top-level role buckets, each holding the projects of one role: `core/` (contracts + engine: `Dmon.Abstractions`, `Dmon.Protocol`, `Dmon.Core`, `Dmon.Runtime`, `Dmon.Protocol.SchemaGen`), `providers/` (provider packages), `tools/` (tool packages), `memory/` (memory backend implementations + the `IMemory` facade), `middleware/` (ADR-023 chat-pipeline middleware packages), `frontends/` (protocol-surface host apps), `daemon/` (the Daemon personal-assistant *composition*: the `Daemon.cs` composition root, the `Daemon.Routing` triage-policy library, and the `Daemon.App` Swift menu bar app — ADR-028), `services/` (standalone backing **server** apps that pair with a `tools/` extension — e.g. the `Dcal` iCal-sync server; app artifacts, independently versioned, not on the protocol-lockstep train — ADR-028), and `samples/` (composition-root examples + the prebuilt stock default core). The memory **contracts** (`Dmon.Abstractions.Memory`) remain part of `core/`; only memory **implementations** live under `memory/`. A role bucket with no current members (e.g. `middleware/` until the first `IDmonMiddleware` ships) SHALL NOT exist as a directory; the role remains defined and its bucket materialises with its first member. No first-party project SHALL remain under a flat `src/` or a top-level `extensions/` directory.

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

### Requirement: Per-area solutions and a root superset

The repository SHALL provide one `.slnx` per area that has C# members (`core.slnx`, `providers.slnx`, `tools.slnx`, `memory.slnx`, `frontends.slnx`, `daemon.slnx`, `services.slnx`) and a root `Everything.slnx` that includes every first-party and test .NET project. An area with no C# member projects SHALL NOT carry a `.slnx`. Swift packages (e.g. `daemon/Daemon.App`) are **not** .NET projects and SHALL NOT be referenced by any `.slnx`; they are built via their own toolchain (`make daemon-app`). The standalone `Dmon.slnx` SHALL NOT exist.

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

### Requirement: Intra-repo references use ProjectReference

Solution-build projects within the repository SHALL reference each other via `ProjectReference`, never `PackageReference`, so cross-area changes are atomic and the global NuGet cache is not involved in everyday builds. Consumer-simulation fixtures under `samples/` are exempt: they exist to verify the *published package surface* and therefore deliberately consume first-party packages via `PackageReference` (against a local feed); they are excluded from the area `.slnx` and `Everything.slnx` solutions.

#### Scenario: No intra-repo PackageReference

- **WHEN** the `.csproj` files of all projects included in any `.slnx` solution are inspected
- **THEN** no such project declares a `PackageReference` to another first-party project in the repository (`Dmon.*` / `dmoncore`)
- **AND** the only first-party `PackageReference`s in the repository are in the `samples/` consumer-simulation fixtures, which are excluded from the solutions

### Requirement: Nested build configuration

The repository SHALL centrally manage third-party package versions via a root `Directory.Packages.props` (rather than inline `.csproj` pins) and SHALL apply shared build settings from a root `Directory.Build.props` across all projects. Per-area `Directory.Build.props` files are introduced only as an area acquires settings that diverge from the root; when present they SHALL chain-import the root rather than redefine it, so the shared block (MinVer, the SourceLink/MinVer global package references, the protocol skew-guard) continues to apply. No area is required to carry a per-area build-props file in Phase 0.

#### Scenario: Root settings apply across areas

- **WHEN** any project in any bucket is built
- **THEN** the shared root settings (MinVer, SourceLink, symbol packages, `IsPackable` default, skew-guard) are in effect
- **AND** any area-level `Directory.Build.props` that exists imports the root rather than redefining it

#### Scenario: Skew-guard resolves the protocol-version file

- **WHEN** a packable project is built
- **THEN** the protocol-version skew-guard reads `core/Dmon.Protocol/ProtocolVersion.cs` and passes when the package `Major.Minor` matches `ProtocolVersion.Current`

### Requirement: Package naming families

First-party packageable projects SHALL follow the ADR-023 D3 naming families: providers as `Dmon.Providers.<Name>`, tools as `Dmon.Tools.<Name>`, memory backends as `Dmon.Memory.<Name>` (with the local short-term tier as the bare `Dmon.Memory`), and chat-pipeline middleware as `Dmon.Middleware.<Name>`. The Mlx provider SHALL be named `Dmon.Providers.Mlx` (assembly, namespace, and `PackageId`) and SHALL be packable.

#### Scenario: Mlx conforms to the provider family

- **WHEN** the Mlx provider project is inspected
- **THEN** its `PackageId`, assembly name, and root namespace are `Dmon.Providers.Mlx`
- **AND** it resides under `providers/`
- **AND** it is marked packable

#### Scenario: Memory backends conform to the memory family

- **WHEN** the memory projects are inspected
- **THEN** the local short-term tier is `Dmon.Memory` and the Meko long-term backend is `Dmon.Memory.Meko` (assembly, namespace, and `PackageId`)
- **AND** both reside under `memory/`
- **AND** both are marked packable
