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
The system SHALL publish `Dmon.Terminal` as a .NET global tool with command name `dmon`, installable via `dotnet tool install`.

#### Scenario: Tool package is produced
- **WHEN** `dotnet pack` is run on `Dmon.Terminal`
- **THEN** the resulting package is a tool package (`PackAsTool`) whose invocation command is `dmon`

#### Scenario: Tool does not carry the core as a package dependency
- **WHEN** the `dmon` tool package metadata is inspected
- **THEN** it declares no package dependency on `dmoncore` and bundles no `dmoncore` payload

### Requirement: `dmoncore` published as a runnable publish closure
The system SHALL publish `dmoncore` as a NuGet package that contains its full framework-dependent publish output — every dependency assembly plus `dmoncore.deps.json` and `dmoncore.runtimeconfig.json` — so that the package, once present in the global NuGet cache, is runnable directly via `dotnet exec` without a further restore.

#### Scenario: Core package contains a runnable closure
- **WHEN** the `dmoncore` package is unpacked
- **THEN** it contains `dmoncore.dll`, its dependency assemblies, `dmoncore.deps.json`, and `dmoncore.runtimeconfig.json` laid out for direct `dotnet exec`

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

