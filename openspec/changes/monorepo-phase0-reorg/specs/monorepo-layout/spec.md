## ADDED Requirements

### Requirement: Top-level role buckets

The repository SHALL organise first-party projects into top-level role buckets, each holding the projects of one ADR-023 role: `core/` (contracts + engine: `Dmon.Abstractions`, `Dmon.Protocol`, `Dmon.Core`, `Dmon.Runtime`, `Dmon.Protocol.SchemaGen`), `providers/` (provider packages), `tools/` (tool packages), `middleware/` (middleware/memory packages), `frontends/` (host apps), and `samples/` (composition-root examples + the prebuilt stock default core). No first-party project SHALL remain under a flat `src/` or a top-level `extensions/` directory.

#### Scenario: Every solution project lives in a bucket

- **WHEN** the repository is inspected after Phase 0
- **THEN** each project referenced by any `.slnx` resolves to a path under `core/`, `providers/`, `tools/`, `middleware/`, `frontends/`, `samples/`, or `test/`
- **AND** neither `src/` nor `extensions/` contains any tracked first-party project

#### Scenario: No ghost or cruft project directories

- **WHEN** the repository is inspected after Phase 0
- **THEN** the former ghost directories (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui`) do not exist
- **AND** no project directory contains only `bin`/`obj` build artifacts with no source

### Requirement: Per-area solutions and a root superset

The repository SHALL provide one `.slnx` per area (`core.slnx`, `providers.slnx`, `tools.slnx`, `middleware.slnx`, `frontends.slnx`) and a root `Everything.slnx` that includes every first-party and test project. The standalone `Dmon.slnx` SHALL be removed.

#### Scenario: Area solution builds in isolation

- **WHEN** `dotnet build providers.slnx -c Release` is run
- **THEN** the provider projects and their dependencies build successfully without errors or warnings

#### Scenario: Everything solution builds and tests the whole repo

- **WHEN** `make build` and `make test` are run against `Everything.slnx`
- **THEN** the build is clean (no errors, `TreatWarningsAsErrors` satisfied)
- **AND** all existing test projects are discovered and pass

### Requirement: Intra-repo references use ProjectReference

Projects within the repository SHALL reference each other via `ProjectReference`, never `PackageReference`, so cross-area changes are atomic and the global NuGet cache is not involved in everyday builds.

#### Scenario: No intra-repo PackageReference

- **WHEN** all `.csproj` files are inspected
- **THEN** no project declares a `PackageReference` to another first-party project in the repository (`Dmon.*` / `dmoncore`)

### Requirement: Nested build configuration

The repository SHALL use a root `Directory.Build.props` and `Directory.Packages.props` for shared settings, with per-area `Directory.Build.props` files that `Import` the root for area-specific deltas. Third-party package versions SHALL be centrally managed via `Directory.Packages.props` rather than pinned inline in individual `.csproj` files.

#### Scenario: Root settings apply across areas

- **WHEN** any project in any bucket is built
- **THEN** the shared root settings (MinVer, SourceLink, symbol packages, `IsPackable` default) are in effect
- **AND** an area's own `Directory.Build.props` imports the root rather than redefining it

#### Scenario: Skew-guard resolves the protocol-version file

- **WHEN** a packable project is built
- **THEN** the protocol-version skew-guard reads `core/Dmon.Protocol/ProtocolVersion.cs` and passes when the package `Major.Minor` matches `ProtocolVersion.Current`

### Requirement: Package naming families

First-party packageable projects SHALL follow the ADR-023 D3 naming families: providers as `Dmon.Providers.<Name>`, tools as `Dmon.Tools.<Name>`, middleware as `Dmon.Middleware.<Name>`. The Omlx provider SHALL be named `Dmon.Providers.Omlx` (assembly, namespace, and `PackageId`) and SHALL be packable.

#### Scenario: Omlx conforms to the provider family

- **WHEN** the Omlx provider project is inspected after Phase 0
- **THEN** its `PackageId`, assembly name, and root namespace are `Dmon.Providers.Omlx`
- **AND** it resides under `providers/`
- **AND** it is marked packable
