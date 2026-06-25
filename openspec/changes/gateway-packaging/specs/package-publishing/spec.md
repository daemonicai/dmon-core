## ADDED Requirements

### Requirement: `Dmon.Gateway` published as a dotnet tool
The system SHALL publish `Dmon.Gateway` as a .NET global tool (`PackAsTool`) whose installed executable file name is exactly `Dmon.Gateway`, installable via `dotnet tool install`, so that a global install places the binary at `~/.dotnet/tools/Dmon.Gateway` — the default Gateway candidate that `daemon/Daemon.App` (`GatewayManager`) resolves when no override and no `DMON_GATEWAY_PATH` is set. The system SHALL provide a `make gateway` build target that produces and installs this tool locally. The Gateway tool is an **app artifact, independently versioned** (ADR-024/ADR-028): it SHALL NOT be bound to the protocol-keyed `Major.Minor` lockstep that governs the first-party NuGet set, and it is NOT a `Dmon.*` protocol package.

#### Scenario: Gateway tool package is produced
- **WHEN** `dotnet pack` is run on `Dmon.Gateway`
- **THEN** the resulting package is a tool package (`PackAsTool`) whose installed executable file name is `Dmon.Gateway`

#### Scenario: Install lands at the default candidate path
- **WHEN** the Gateway tool is installed with a global `dotnet tool install`
- **THEN** an executable resolvable at `~/.dotnet/tools/Dmon.Gateway` exists, matching the default candidate `GatewayManager` looks for

#### Scenario: `make gateway` produces a resolvable Gateway
- **WHEN** `make gateway` is run on a clean checkout
- **THEN** the Gateway tool is built, packed, and installed such that `~/.dotnet/tools/Dmon.Gateway` resolves, and dmonium's Gateway health row can start the process without any `DMON_GATEWAY_PATH` override

#### Scenario: Gateway tool versions independently of the protocol line
- **WHEN** the Gateway tool package is packed while its own version differs in `Major.Minor` from `ProtocolVersion.Current`
- **THEN** the pack/release process does NOT reject it, because the Gateway is an independently-versioned app artifact, not a protocol-keyed package

## MODIFIED Requirements

### Requirement: Only the five distribution projects are packable
The system SHALL default `IsPackable` to false for all projects and enable it only for the published projects: the contract packages (`Dmon.Protocol`, `Dmon.Abstractions`), the engine (`Dmon.Core`), the tool (`Dmon.Terminal`), each granular first-party implementation package (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`), and the **app-artifact dotnet tools** — currently `Dmon.Gateway` (`PackAsTool`). The original fixed list of five is superseded by this open set as implementation packages are split out (ADR-023 D2) and app-artifact tools are added (ADR-024). App-artifact tools are packable but are NOT part of the protocol-keyed first-party NuGet set (see "Protocol-keyed three-part version scheme"). Internal libraries such as `Dmon.Runtime` SHALL NOT be packable. `Dmon.Extensions` SHALL NOT be a project or a package.

#### Scenario: Internal library is not packed
- **WHEN** a solution-wide pack is run
- **THEN** no package is produced for `Dmon.Runtime` (or any other internal/test project), and packages are produced only for the contract, engine, tool, granular implementation, and app-artifact-tool projects

#### Scenario: No Dmon.Extensions package is produced
- **WHEN** a solution-wide pack is run
- **THEN** no `Dmon.Extensions` package is produced, because the project has been deleted and its contracts moved into `Dmon.Abstractions`

#### Scenario: Gateway is packable as an app-artifact tool
- **WHEN** a solution-wide pack is run
- **THEN** a tool package is produced for `Dmon.Gateway`, separate from the protocol-keyed first-party NuGet set

### Requirement: Protocol-keyed three-part version scheme
Published versions SHALL be three-part `Major.Minor.Patch`, where `Major.Minor` equals the wire-protocol contract version (`Dmon.Protocol.ProtocolVersion.Current`) and `Patch` is the component's own release counter. The contract packages and all first-party implementation packages (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`) SHALL move in lockstep on this protocol-keyed `Major.Minor` line together with `dmoncore` (ADR-023 D5). A packed version whose `Major.Minor` diverges from `ProtocolVersion.Current` SHALL be rejected by the build or release process. **App-artifact dotnet tools (currently `Dmon.Gateway`) are exempt from this protocol-keyed gate**: they are independently versioned on their own cadence (ADR-024) and their `Major.Minor` is NOT required to equal `ProtocolVersion.Current`. Third-party packages SHALL NOT be bound to this lockstep; they pin `Dmon.Abstractions@X.Y.*` and version on their own cadence.

#### Scenario: Version major.minor tracks the protocol
- **WHEN** a package is built while `ProtocolVersion.Current` is `0.1`
- **THEN** the package version's `Major.Minor` is `0.1`, and a version with a differing `Major.Minor` fails the version-consistency check

#### Scenario: First-party packages move in lockstep
- **WHEN** the protocol line advances and the first-party set (`dmoncore`, contracts, every `Dmon.Providers.<Name>` / `Dmon.Tools.<Name>` / `Dmon.Middleware.<Name>`) is packed
- **THEN** every first-party package carries the same `Major.Minor` so an authored `Dmon.cs` pinning `@<protocol>.*` resolves one coherent dependency graph

#### Scenario: App-artifact tool is exempt from the protocol gate
- **WHEN** the `Dmon.Gateway` tool package is packed with a `Major.Minor` that differs from `ProtocolVersion.Current`
- **THEN** the version-consistency check does NOT reject it, because app-artifact tools version independently of the protocol line
