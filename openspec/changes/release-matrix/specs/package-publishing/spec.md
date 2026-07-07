## MODIFIED Requirements

### Requirement: Only the five distribution projects are packable
The system SHALL default `IsPackable` to false for all projects and enable it only for the published projects: the contract packages (`Dmon.Protocol`, `Dmon.Abstractions`), the engine (`Dmon.Core`), each granular first-party implementation package (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`), the **memory backend packages** (`Dmon.Memory`, `Dmon.Memory.Meko`), and the **dotnet tools** (`Dmon.Terminal`/`dmon` and `Dmon.Network`/`ndmon`, both `PackAsTool`). The original fixed list of five is superseded by this open set as implementation packages are split out (ADR-023 D2) and the memory bucket and tool packages join the NuGet family (ADR-035 D4/D5). All of these are members of the protocol-keyed first-party NuGet set (see "Protocol-keyed three-part version scheme"). Internal libraries such as `Dmon.Runtime` SHALL NOT be packable. `Dmon.Extensions` SHALL NOT be a project or a package.

#### Scenario: Internal library is not packed
- **WHEN** a solution-wide pack is run
- **THEN** no package is produced for `Dmon.Runtime` (or any other internal/test project), and packages are produced only for the contract, engine, granular implementation, memory-backend, and dotnet-tool projects

#### Scenario: No Dmon.Extensions package is produced
- **WHEN** a solution-wide pack is run
- **THEN** no `Dmon.Extensions` package is produced, because the project has been deleted and its contracts moved into `Dmon.Abstractions`

#### Scenario: Memory backends are packable
- **WHEN** a solution-wide pack is run
- **THEN** a package is produced for `Dmon.Memory` (with an explicit `PackageId`) and for `Dmon.Memory.Meko`, both in the protocol-keyed first-party NuGet set

#### Scenario: Network tool is packable on the protocol line
- **WHEN** a solution-wide pack is run
- **THEN** a tool package is produced for `Dmon.Network` (`ndmon`), as a member of the protocol-keyed first-party NuGet set

### Requirement: Protocol-keyed three-part version scheme
Published versions SHALL be three-part `Major.Minor.Patch`, where `Major.Minor` equals the wire-protocol contract version (`Dmon.Protocol.ProtocolVersion.Current`) and `Patch` is the component's own release counter. The entire **NuGet family** — the contract packages, `dmoncore`, every first-party implementation package (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`), the memory backends (`Dmon.Memory`, `Dmon.Memory.Meko`), and the `PackAsTool` dotnet tools (`dmon`, `ndmon`) — SHALL move in lockstep on this protocol-keyed `Major.Minor` line; only `Patch` is independent per package (ADR-023 D5, ADR-035 D1/D4). A packed version whose `Major.Minor` diverges from `ProtocolVersion.Current` SHALL be rejected by the build or release process. The **app-artifact family** (non-NuGet bundles — the dmonium macOS app and the `Dmon.Desktop` bundle) is NOT bound to the restore-time protocol gate; those artifacts version on their own cadence and enforce protocol compatibility at runtime via the `agentReady` handshake (ADR-035 D3). Third-party packages SHALL NOT be bound to the lockstep; they pin `Dmon.Abstractions@X.Y.*` and version on their own cadence.

#### Scenario: Version major.minor tracks the protocol
- **WHEN** a package is built while `ProtocolVersion.Current` is `0.1`
- **THEN** the package version's `Major.Minor` is `0.1`, and a version with a differing `Major.Minor` fails the version-consistency check

#### Scenario: First-party NuGet set moves in lockstep
- **WHEN** the protocol line advances and the NuGet family (`dmoncore`, contracts, every implementation package, the memory backends, and the `dmon`/`ndmon` tools) is packed
- **THEN** every one carries the same `Major.Minor` so an authored `Dmon.cs` pinning `@<protocol>.*` resolves one coherent dependency graph

#### Scenario: NuGet dotnet tool is on the protocol line
- **WHEN** the `Dmon.Network` (`ndmon`) tool package is packed while `ProtocolVersion.Current` is `0.2`
- **THEN** its `Major.Minor` is `0.2` (it is a nuget.org dotnet tool on the lockstep, not exempt), and only its `Patch` differs from other packages independently

#### Scenario: Non-NuGet app artifact versions independently
- **WHEN** the dmonium macOS app artifact is versioned
- **THEN** it is not subject to the restore-time protocol-`Major.Minor` gate and enforces protocol compatibility at runtime via the `agentReady` handshake instead

### Requirement: Tag-driven release pipeline
The system SHALL provide a tag-triggered release workflow that publishes packages and artifacts on **per-package tags of the form `<area>/<name>-v<X.Y.Z>`** (ADR-035 D1). The workflow SHALL map a pushed tag to its target project(s) via the single shared area→paths map (ADR-035 D6, the same map the CI path-filter uses). For NuGet-family tags it SHALL run `dotnet pack` + `dotnet nuget push` to nuget.org using a `NUGET_API_KEY` secret (including `.snupkg` symbols where produced). The pull-request CI SHALL NOT publish. Every NuGet-family package (ADR-035 D7) SHALL have a release path; the legacy `sdk-*`/`dmon-*`/`core-*` tag lines are retired. A protocol-cycle boundary SHALL be releasable as a wave that tags every NuGet-family package at `<prefix>X.Y.0` — including unchanged packages — so that `@X.Y.*` always resolves (ADR-035 D2).

#### Scenario: Publish a single package on its per-package tag
- **WHEN** a tag `providers/anthropic-v0.2.5` is pushed
- **THEN** the release workflow packs and pushes only `Dmon.Providers.Anthropic` at `0.2.5` to nuget.org

#### Scenario: Pull request never publishes
- **WHEN** a pull request is opened
- **THEN** no package or artifact is published

#### Scenario: Cycle wave re-releases the whole set
- **WHEN** a protocol cycle opens at `X.Y` and the cycle-wave release is run
- **THEN** every NuGet-family package is tagged and published at `X.Y.0`, including packages with no source change since the previous cycle

## ADDED Requirements

### Requirement: App-artifact release family
Non-NuGet first-party artifacts — the dmonium macOS app (`daemon/Daemon.App`, `.app`/`.dmg`) and the `Dmon.Desktop` bundle — SHALL be released as the **app-artifact family**: built by dedicated packaging jobs and published as attachments to a **GitHub Release** (not nuget.org), triggered by `app/<name>-v<X.Y.Z>` tags (ADR-035 D3). Each app artifact SHALL carry an `X.Y.Z` version and SHALL enforce wire-protocol compatibility at runtime via the `agentReady` `protocolVersion` handshake rather than at restore time.

#### Scenario: App artifact publishes to a GitHub Release
- **WHEN** a tag `app/dmonium-v0.2.0` is pushed
- **THEN** a packaging job builds the dmonium bundle and attaches it to a GitHub Release, and nothing is pushed to nuget.org for that tag

#### Scenario: App artifact enforces protocol at runtime
- **WHEN** an app-artifact host connects to a core whose `protocolVersion` is incompatible
- **THEN** the `agentReady` handshake rejects the mismatch at runtime (the app artifact is not gated at restore time)
