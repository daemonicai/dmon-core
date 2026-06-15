# package-publishing Specification

## Purpose
TBD - created by archiving change core-distribution. Update Purpose after archive.
## Requirements
### Requirement: Granular SDK contract packages
The system SHALL publish `Dmon.Protocol`, `Dmon.Abstractions`, and `Dmon.Extensions` as three separate NuGet packages so out-of-tree extension authors can compile against the contract. Each SHALL be marked packable and SHALL declare its inter-package dependencies (`Dmon.Extensions` and `Dmon.Abstractions` depend on `Dmon.Protocol`) rather than embedding the referenced assemblies.

#### Scenario: SDK packages pack independently
- **WHEN** `dotnet pack` is run on `Dmon.Protocol`, `Dmon.Abstractions`, and `Dmon.Extensions`
- **THEN** three distinct `.nupkg` files are produced, and the `Dmon.Extensions` and `Dmon.Abstractions` packages each declare a package dependency on `Dmon.Protocol` (not a copy of `Dmon.Protocol.dll`)

#### Scenario: An out-of-tree project compiles against the packages
- **WHEN** a project references the published `Dmon.Extensions` package and implements `IDmonExtension`
- **THEN** it compiles using only the package references, with no `ProjectReference` into this repository

### Requirement: `dmon` published as a dotnet tool
The system SHALL publish `Dmon.Terminal` as a .NET global tool with command name `dmon`, installable via `dotnet tool install`. The tool package SHALL **bundle the prebuilt default core** (the canonical-`Dmon.cs` publish closure) as a file payload, so a first run in an empty directory works offline on the .NET runtime alone with no SDK and no network — this replaces the retired runtime acquisition path. The tool SHALL declare no NuGet package *dependency* on `dmoncore`; the core arrives as a bundled payload, version-aligned to the tool's protocol line, not via NuGet restore.

#### Scenario: Tool package is produced
- **WHEN** `dotnet pack` is run on `Dmon.Terminal`
- **THEN** the resulting package is a tool package (`PackAsTool`) whose invocation command is `dmon`

#### Scenario: Tool bundles the prebuilt default core, not a package dependency
- **WHEN** the `dmon` tool package is inspected
- **THEN** it contains the prebuilt default-core payload (runnable via `dotnet exec` with no restore) and declares no NuGet package dependency on `dmoncore`

#### Scenario: First run works offline with no SDK
- **WHEN** `dmon` is invoked in an empty directory with no SDK and no network
- **THEN** it launches the bundled prebuilt default core and serves a turn

### Requirement: `dmoncore` published as a runnable publish closure
`dmoncore` SHALL be published as a **library** NuGet package — the `#:package`-able unit a `Dmon.cs` composition root references (`composition-root-hosting`), declaring its dependencies as package references rather than embedding a runnable closure. Separately, dmon SHALL ship a **prebuilt default core**: a framework-dependent publish closure of dmon's *canonical* `Dmon.cs` (which references the `dmoncore` library with no extra extensions), runnable directly via `dotnet exec` with no SDK and no restore, serving the no-SDK / first-run path (`core-runtime-acquisition` discovery precedence). The library is the unit of distribution; the prebuilt closure is a convenience artifact derived from it (ADR-019 Decision 9).

#### Scenario: dmoncore package is a referenceable library
- **WHEN** the `dmoncore` package is inspected
- **THEN** it is a library package that a `Dmon.cs` can reference via `#:package dmoncore@<protocol>.*`, declaring its dependencies as package references (not a self-contained runnable closure)

#### Scenario: A prebuilt default core is shipped runnable
- **WHEN** the prebuilt default-core artifact is unpacked
- **THEN** it contains `dmoncore.dll` (built from the canonical `Dmon.cs`), its dependency assemblies, `deps.json`, and `runtimeconfig.json` laid out for direct `dotnet exec` with no further restore

### Requirement: Only the five distribution projects are packable
The system SHALL default `IsPackable` to false for all projects and enable it only for the five published projects (`Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`, `Dmon.Terminal`, `Dmon.Core`). Internal libraries such as `Dmon.Runtime` SHALL NOT be packable.

#### Scenario: Internal library is not packed
- **WHEN** a solution-wide pack is run
- **THEN** no package is produced for `Dmon.Runtime` (or any other internal/test project), and packages are produced only for the five distribution projects

### Requirement: Package license and metadata
Every published package SHALL declare `PackageLicenseExpression` `MPL-2.0`, and the repository SHALL contain a corresponding `LICENSE` file. Published packages SHALL carry shared metadata (authors, repository URL, deterministic build, symbol package, SourceLink) sourced from a central `Directory.Build.props`.

#### Scenario: License expression present on every package
- **WHEN** any published package is inspected
- **THEN** its license expression is `MPL-2.0` and a `LICENSE` file exists at the repository root

### Requirement: Protocol-keyed three-part version scheme
Published versions SHALL be three-part `Major.Minor.Patch`, where `Major.Minor` equals the wire-protocol contract version (`Dmon.Protocol.ProtocolVersion.Current`) and `Patch` is the component's own release counter. A packed version whose `Major.Minor` diverges from `ProtocolVersion.Current` SHALL be rejected by the build or release process.

#### Scenario: Version major.minor tracks the protocol
- **WHEN** a package is built while `ProtocolVersion.Current` is `0.1`
- **THEN** the package version's `Major.Minor` is `0.1`, and a version with a differing `Major.Minor` fails the version-consistency check

### Requirement: Tag-driven release pipeline
The system SHALL provide a tag-triggered release workflow that runs `dotnet pack` and `dotnet nuget push` to nuget.org using a `NUGET_API_KEY` secret. The pull-request CI SHALL NOT publish packages.

#### Scenario: Publish only on a release tag
- **WHEN** a release tag is pushed
- **THEN** the release workflow packs and pushes the distribution packages to nuget.org
- **AND WHEN** a pull request is opened
- **THEN** no package is published

