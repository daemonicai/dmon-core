## MODIFIED Requirements

### Requirement: Core discovery precedence
`Dmon.Runtime` SHALL resolve the core in a fixed precedence order: (1) a `Dmon.cs` composition root in the working directory (built and run per "Launch the core"); (2) an explicit override — `--core-path` or `DMON_CORE_PATH` — naming a prebuilt core; (3) the built-in **default core**, a prebuilt publish of dmon's canonical `Dmon.cs` (`package-publishing`), so an empty directory runs on the .NET runtime alone with no SDK and no network. There is no NuGet-cache or on-demand-acquisition tier. The override remains a working offline/dev escape hatch and wins over the built-in default.

#### Scenario: A Dmon.cs in the working directory wins
- **WHEN** the working directory contains a `Dmon.cs` and an override is also set
- **THEN** `Dmon.Runtime` composes from `Dmon.cs` (the user-authored composition root takes precedence)

#### Scenario: Override wins over the built-in default
- **WHEN** no `Dmon.cs` is present and `--core-path` (or `DMON_CORE_PATH`) names a prebuilt core
- **THEN** `Dmon.Runtime` launches the override-specified core and does not fall through to the built-in default

#### Scenario: Empty directory uses the prebuilt default
- **WHEN** no `Dmon.cs` and no override are present
- **THEN** `Dmon.Runtime` launches the built-in prebuilt default core with no SDK requirement and no network request

### Requirement: Version resolution by protocol compatibility
The compatible `dmoncore` version SHALL be expressed as a protocol-keyed `#:package` pin in `Dmon.cs` — `#:package dmoncore@<Major.Minor>.*` where `Major.Minor` equals `ProtocolVersion.Current` — and resolved by `dotnet restore` to the newest `dmoncore` within that `Major.Minor`. Selection is performed by the SDK over the `#:package` range, not a runtime resolver, and SHALL NOT cross `Major.Minor`. The prebuilt default core is pinned the same way at build time.

#### Scenario: Newest compatible core is restored
- **WHEN** `Dmon.cs` declares `#:package dmoncore@0.1.*` and nuget.org offers `dmoncore` `0.1.3`, `0.1.9`, and `0.2.0`
- **THEN** `dotnet restore` resolves `0.1.9` and never `0.2.0`

### Requirement: Launch the core from its publish closure
For a `Dmon.cs` composition root, `Dmon.Runtime` SHALL launch the core in two steps so the build phase never shares the JSONL/stdio channel: first run `dotnet build Dmon.cs` as a *separate* process whose stdout/stderr it captures or discards (the SDK's incremental up-to-date check is the staleness gate), then launch `dotnet run Dmon.cs --no-build` as the stdio child — `--no-build` skips the build phase (and implies `--no-restore`), so no MSBuild/restore output can precede the program on stdout. For the built-in default and any prebuilt override, `Dmon.Runtime` SHALL `dotnet exec` the prebuilt `dmoncore.dll` directly, relying on its bundled `deps.json`/`runtimeconfig.json`. In all cases it connects to the child over JSONL/stdio.

#### Scenario: Dmon.cs path keeps the build off the wire
- **WHEN** `Dmon.Runtime` launches from a `Dmon.cs` (including on `/reload`)
- **THEN** it builds in a separate captured step, then runs `dotnet run Dmon.cs --no-build`, and the child's stdout carries only JSONL frames (no build or restore output)

#### Scenario: Prebuilt core is exec'd directly
- **WHEN** the resolved core is the built-in default or a prebuilt override
- **THEN** `Dmon.Runtime` starts it via `dotnet exec` of the prebuilt `dmoncore.dll` and exposes its stdio for the host's RPC loop

## REMOVED Requirements

### Requirement: On-demand acquisition into the global NuGet cache
**Reason**: ADR-019 removes dmon's bespoke runtime NuGet downloader (supersedes ADR-011 D3). Acquisition of `dmoncore` and extensions is `dotnet restore` over the `Dmon.cs` `#:package` set, performed by the SDK at build time; the no-SDK / first-run case is served by the prebuilt default core, not a download.
**Migration**: Author or scaffold (`dmon init`) a `Dmon.cs` whose `#:package dmoncore@<protocol>.*` is restored by the SDK; offline/no-SDK use falls back to the prebuilt default core or a `--core-path`/`DMON_CORE_PATH` override.
