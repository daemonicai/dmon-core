## MODIFIED Requirements

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
