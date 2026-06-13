## MODIFIED Requirements

### Requirement: `dmoncore` published as a runnable publish closure
`dmoncore` SHALL be published as a **library** NuGet package — the `#:package`-able unit a `Dmon.cs` composition root references (`composition-root-hosting`), declaring its dependencies as package references rather than embedding a runnable closure. Separately, dmon SHALL ship a **prebuilt default core**: a framework-dependent publish closure of dmon's *canonical* `Dmon.cs` (which references the `dmoncore` library with no extra extensions), runnable directly via `dotnet exec` with no SDK and no restore, serving the no-SDK / first-run path (`core-runtime-acquisition` discovery precedence). The library is the unit of distribution; the prebuilt closure is a convenience artifact derived from it (ADR-019 Decision 9).

#### Scenario: dmoncore package is a referenceable library
- **WHEN** the `dmoncore` package is inspected
- **THEN** it is a library package that a `Dmon.cs` can reference via `#:package dmoncore@<protocol>.*`, declaring its dependencies as package references (not a self-contained runnable closure)

#### Scenario: A prebuilt default core is shipped runnable
- **WHEN** the prebuilt default-core artifact is unpacked
- **THEN** it contains `dmoncore.dll` (built from the canonical `Dmon.cs`), its dependency assemblies, `deps.json`, and `runtimeconfig.json` laid out for direct `dotnet exec` with no further restore
