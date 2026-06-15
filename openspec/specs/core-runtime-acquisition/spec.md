# core-runtime-acquisition Specification

## Purpose
TBD - created by archiving change core-distribution. Update Purpose after archive.
## Requirements
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

### Requirement: Protocol-version compatibility gate at handshake
`Dmon.Runtime` SHALL read the `protocolVersion` reported by the core in its `agentReady` event and SHALL verify that its `Major.Minor` matches `ProtocolVersion.Current`. On mismatch it SHALL fail with a clear, actionable error rather than proceeding. The wire protocol version SHALL be sourced from a single `Dmon.Protocol.ProtocolVersion.Current` constant, which the core emits at `agentReady`.

#### Scenario: Matching protocol proceeds
- **WHEN** the core's `agentReady.protocolVersion` has the same `Major.Minor` as `ProtocolVersion.Current`
- **THEN** `Dmon.Runtime` proceeds and hands the running, connected core to the host

#### Scenario: Mismatched protocol is rejected
- **WHEN** the core's `agentReady.protocolVersion` `Major.Minor` differs from `ProtocolVersion.Current` (e.g. a stale cached core or an overridden incompatible binary)
- **THEN** `Dmon.Runtime` stops the core and fails with an actionable protocol-mismatch error instead of starting a turn

### Requirement: Reusable, host-agnostic runtime library
Core discovery, acquisition, process lifecycle, and the compatibility gate SHALL live in an internal `Dmon.Runtime` library that contains no console- or UI-specific code, so that any host can consume it. `Dmon.Terminal` SHALL obtain its core process through `Dmon.Runtime` rather than implementing discovery or lifecycle itself.

#### Scenario: Terminal delegates core bootstrap to the runtime library
- **WHEN** `Dmon.Terminal` starts the core (including on `/reload` restart)
- **THEN** it does so through `Dmon.Runtime`, and `Dmon.Runtime` carries no dependency on console/TUI rendering libraries

