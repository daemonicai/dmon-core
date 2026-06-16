## Purpose

Define the standing structural contract for the dmon monorepo (ADR-025): the top-level role buckets and what each holds, the per-area `.slnx` + root `Everything.slnx` requirement, the intra-repo `ProjectReference` rule (and the `samples/` consumer-simulation exemption), the nested `Directory.Build.props` / central `Directory.Packages.props` arrangement, and the ADR-023 D3 package naming families. Future changes (satellite grafts, new packages) must conform to it.

## Requirements

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

First-party packageable projects SHALL follow the ADR-023 D3 naming families: providers as `Dmon.Providers.<Name>`, tools as `Dmon.Tools.<Name>`, middleware as `Dmon.Middleware.<Name>`. The Omlx provider SHALL be named `Dmon.Providers.Omlx` (assembly, namespace, and `PackageId`) and SHALL be packable.

#### Scenario: Omlx conforms to the provider family

- **WHEN** the Omlx provider project is inspected after Phase 0
- **THEN** its `PackageId`, assembly name, and root namespace are `Dmon.Providers.Omlx`
- **AND** it resides under `providers/`
- **AND** it is marked packable
