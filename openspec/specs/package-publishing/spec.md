# package-publishing Specification

## Purpose
TBD - created by archiving change core-distribution. Update Purpose after archive.
## Requirements
### Requirement: Granular SDK contract packages
The system SHALL publish `Dmon.Protocol` and `Dmon.Abstractions` as two separate NuGet packages so out-of-tree extension authors can compile against the contract. `Dmon.Extensions` SHALL NOT be published; all author-facing contracts collapse into the single `Dmon.Abstractions` package (ADR-022 D12, ADR-023 D2). Each published contract package SHALL be marked packable and SHALL declare its inter-package dependencies (`Dmon.Abstractions` depends on `Dmon.Protocol`) rather than embedding the referenced assemblies. The SDK base an extension author references SHALL be `Dmon.Protocol` + `Dmon.Abstractions` + `dmoncore`.

#### Scenario: SDK packages pack independently
- **WHEN** `dotnet pack` is run on `Dmon.Protocol` and `Dmon.Abstractions`
- **THEN** two distinct `.nupkg` files are produced, the `Dmon.Abstractions` package declares a package dependency on `Dmon.Protocol` (not a copy of `Dmon.Protocol.dll`), and no `Dmon.Extensions` package is produced

#### Scenario: An out-of-tree project compiles against the packages
- **WHEN** a project references the published `Dmon.Abstractions` package and implements `IToolExtension`
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

### Requirement: `Dmon.Network` published as a dotnet tool
The system SHALL publish `Dmon.Network` (the renamed WebSocket remote-access host, formerly `Dmon.Gateway`) as a .NET global tool (`PackAsTool`) with command name **`ndmon`**, installable via `dotnet tool install`, so that a global install places the executable at `~/.dotnet/tools/ndmon` — the default candidate that `daemon/Daemon.App` (`NetworkManager`) resolves when no override and no `DMON_NETWORK_PATH` is set. The system SHALL provide a `make network` build target that produces and installs this tool locally. The Network tool is an **app artifact, independently versioned** (ADR-024): it SHALL NOT be bound to the protocol-keyed `Major.Minor` lockstep that governs the first-party NuGet set, and it is NOT a `Dmon.*` protocol package.

#### Scenario: Network tool package is produced
- **WHEN** `dotnet pack` is run on `Dmon.Network`
- **THEN** the resulting package is a tool package (`PackAsTool`) whose invocation command is `ndmon`

#### Scenario: Install lands at the default candidate path
- **WHEN** the Network tool is installed with a global `dotnet tool install`
- **THEN** an executable resolvable at `~/.dotnet/tools/ndmon` exists, matching the default candidate `NetworkManager` looks for

#### Scenario: `make network` produces a resolvable host
- **WHEN** `make network` is run on a clean checkout
- **THEN** the Network tool is built, packed, and installed such that `~/.dotnet/tools/ndmon` resolves, and dmonium's Network health row can start the process without any `DMON_NETWORK_PATH` override

#### Scenario: Network tool versions independently of the protocol line
- **WHEN** the Network tool package is packed while its own version differs in `Major.Minor` from `ProtocolVersion.Current`
- **THEN** the pack/release process does NOT reject it, because the Network host is an independently-versioned app artifact, not a protocol-keyed package

### Requirement: `dmoncore` published as a runnable publish closure
`dmoncore` SHALL be published as a **library** NuGet package — the `#:package`-able unit a `Dmon.cs` composition root references (`composition-root-hosting`), declaring its dependencies as package references rather than embedding a runnable closure, and depending only on the contract packages (no vendor SDK; see "Vendor-SDK-free engine"). Separately, dmon SHALL ship a **prebuilt stock default core**: a framework-dependent publish closure of dmon's *canonical* `Dmon.cs` that `#:package`s `dmoncore` + the cloud provider packages (`Dmon.Providers.Anthropic`, `Dmon.Providers.OpenAI`, `Dmon.Providers.Gemini`) + `Dmon.Tools.Builtin`, runnable directly via `dotnet exec` with no SDK and no restore, serving the no-SDK / first-run path (`core-runtime-acquisition` discovery precedence). The library is the unit of distribution; the prebuilt closure is a convenience artifact derived from it (ADR-019 Decision 9, ADR-023 D8).

#### Scenario: dmoncore package is a referenceable library
- **WHEN** the `dmoncore` package is inspected
- **THEN** it is a library package that a `Dmon.cs` can reference via `#:package dmoncore@<protocol>.*`, declaring its dependencies as package references (not a self-contained runnable closure)

#### Scenario: A prebuilt stock default core is shipped runnable
- **WHEN** the prebuilt default-core artifact is unpacked
- **THEN** it contains the publish closure of the canonical `Dmon.cs` — `dmoncore.dll`, the cloud provider package assemblies, `Dmon.Tools.Builtin`, their dependency assemblies, `deps.json`, and `runtimeconfig.json` — laid out for direct `dotnet exec` with no further restore

### Requirement: Only the five distribution projects are packable
The system SHALL default `IsPackable` to false for all projects and enable it only for the published projects: the contract packages (`Dmon.Protocol`, `Dmon.Abstractions`), the engine (`Dmon.Core`), the tool (`Dmon.Terminal`), each granular first-party implementation package (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`), and the **app-artifact dotnet tools** — currently `Dmon.Network` (`PackAsTool`, command `ndmon`). The original fixed list of five is superseded by this open set as implementation packages are split out (ADR-023 D2) and app-artifact tools are added (ADR-024). App-artifact tools are packable but are NOT part of the protocol-keyed first-party NuGet set (see "Protocol-keyed three-part version scheme"). Internal libraries such as `Dmon.Runtime` SHALL NOT be packable. `Dmon.Extensions` SHALL NOT be a project or a package.

#### Scenario: Internal library is not packed
- **WHEN** a solution-wide pack is run
- **THEN** no package is produced for `Dmon.Runtime` (or any other internal/test project), and packages are produced only for the contract, engine, tool, granular implementation, and app-artifact-tool projects

#### Scenario: No Dmon.Extensions package is produced
- **WHEN** a solution-wide pack is run
- **THEN** no `Dmon.Extensions` package is produced, because the project has been deleted and its contracts moved into `Dmon.Abstractions`

#### Scenario: Network host is packable as an app-artifact tool
- **WHEN** a solution-wide pack is run
- **THEN** a tool package is produced for `Dmon.Network`, separate from the protocol-keyed first-party NuGet set

### Requirement: Package license and metadata
Every published package SHALL declare `PackageLicenseExpression` `MPL-2.0`, and the repository SHALL contain a corresponding `LICENSE` file. Published packages SHALL carry shared metadata (authors, repository URL, deterministic build, symbol package, SourceLink) sourced from a central `Directory.Build.props`.

#### Scenario: License expression present on every package
- **WHEN** any published package is inspected
- **THEN** its license expression is `MPL-2.0` and a `LICENSE` file exists at the repository root

### Requirement: Protocol-keyed three-part version scheme
Published versions SHALL be three-part `Major.Minor.Patch`, where `Major.Minor` equals the wire-protocol contract version (`Dmon.Protocol.ProtocolVersion.Current`) and `Patch` is the component's own release counter. The contract packages and all first-party implementation packages (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, `Dmon.Middleware.<Name>`) SHALL move in lockstep on this protocol-keyed `Major.Minor` line together with `dmoncore` (ADR-023 D5). A packed version whose `Major.Minor` diverges from `ProtocolVersion.Current` SHALL be rejected by the build or release process. **App-artifact dotnet tools (currently `Dmon.Network`/`ndmon`) are exempt from this protocol-keyed gate**: they are independently versioned on their own cadence (ADR-024) and their `Major.Minor` is NOT required to equal `ProtocolVersion.Current`. Third-party packages SHALL NOT be bound to this lockstep; they pin `Dmon.Abstractions@X.Y.*` and version on their own cadence.

#### Scenario: Version major.minor tracks the protocol
- **WHEN** a package is built while `ProtocolVersion.Current` is `0.1`
- **THEN** the package version's `Major.Minor` is `0.1`, and a version with a differing `Major.Minor` fails the version-consistency check

#### Scenario: First-party packages move in lockstep
- **WHEN** the protocol line advances and the first-party set (`dmoncore`, contracts, every `Dmon.Providers.<Name>` / `Dmon.Tools.<Name>` / `Dmon.Middleware.<Name>`) is packed
- **THEN** every first-party package carries the same `Major.Minor` so an authored `Dmon.cs` pinning `@<protocol>.*` resolves one coherent dependency graph

#### Scenario: App-artifact tool is exempt from the protocol gate
- **WHEN** the `Dmon.Network` tool package is packed with a `Major.Minor` that differs from `ProtocolVersion.Current`
- **THEN** the version-consistency check does NOT reject it, because app-artifact tools version independently of the protocol line

### Requirement: Tag-driven release pipeline
The system SHALL provide a tag-triggered release workflow that runs `dotnet pack` and `dotnet nuget push` to nuget.org using a `NUGET_API_KEY` secret. The pull-request CI SHALL NOT publish packages.

#### Scenario: Publish only on a release tag
- **WHEN** a release tag is pushed
- **THEN** the release workflow packs and pushes the distribution packages to nuget.org
- **AND WHEN** a pull request is opened
- **THEN** no package is published

### Requirement: Vendor-SDK-free engine
`dmoncore` (`Dmon.Core`) SHALL be a vendor-SDK-free engine: it SHALL reference only the contract packages (`Dmon.Abstractions`, `Dmon.Protocol`) and SHALL NOT reference any vendor LLM SDK (e.g. `Anthropic`, `GeminiDotnet`, `Microsoft.Extensions.AI.OpenAI`, `OllamaSharp`) nor any concrete provider/tool/middleware implementation package. The `AddDmonProviders()` aggregate registration SHALL be removed; provider composition is performed by per-package `Use<Provider>` verbs and build-time DI-discovery (ADR-022 D5/D7, ADR-023 D1). The engine retains only the turn loop, RPC, session storage, permission/middleware pipelines, registries, and the hosting builder.

#### Scenario: Engine references no vendor SDK
- **WHEN** the `dmoncore` package's dependencies are inspected
- **THEN** they list only `Dmon.Abstractions` and `Dmon.Protocol` (plus framework dependencies), with no vendor LLM SDK and no concrete provider/tool/middleware package

#### Scenario: AddDmonProviders is gone
- **WHEN** an author compiles a `Dmon.cs` that calls `AddDmonProviders()`
- **THEN** the call does not resolve, because the aggregate registration has been removed in favour of per-provider `Use<Provider>` verbs

#### Scenario: A minimal agent restores minimal dependencies
- **WHEN** a `Dmon.cs` `#:package`s only `dmoncore` and a single provider package (e.g. `Dmon.Providers.Ollama`)
- **THEN** `dotnet restore` pulls that provider's SDK only and never restores the OpenAI, Anthropic, or Gemini SDKs

### Requirement: Granular first-party implementation packages
Each provider, tool, and middleware SHALL ship as its own granular NuGet package rather than being sealed inside the engine. First-party families SHALL be named `Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, and `Dmon.Middleware.<Name>` (e.g. `Dmon.Providers.Anthropic`, `Dmon.Providers.OpenAI`, `Dmon.Providers.Gemini`, `Dmon.Providers.Ollama`, `Dmon.Tools.Builtin`). Each implementation package SHALL reference `Dmon.Abstractions` plus only the vendor SDK it itself needs, and SHALL expose its fluent `Use*`/`Add*` verb in the `Dmon.Hosting` namespace so the verb becomes visible to an authored `Dmon.cs` after a single `#:package` line with no additional `using` (ADR-023 D2/D4). First-party and third-party packages SHALL be structurally identical in shape.

#### Scenario: Provider package carries only its own SDK
- **WHEN** the `Dmon.Providers.Anthropic` package's dependencies are inspected
- **THEN** they list `Dmon.Abstractions` and the Anthropic vendor SDK, and do not list any other provider's vendor SDK

#### Scenario: Verb appears after a single package line
- **WHEN** an authored `Dmon.cs` adds `#:package Dmon.Providers.Gemini@<protocol>.*` and already has `using Dmon.Hosting;`
- **THEN** the package's fluent verb (e.g. `UseGemini`) is in scope with no further `using` directive

### Requirement: Third-party package naming convention
Third-party implementation packages SHALL pin `Dmon.Abstractions@X.Y.*` and SHALL be free to version on their own cadence (they are not bound to the first-party lockstep). The recommended (non-enforced) naming convention for third-party packages SHALL be `<Owner>.Dmon.<Name>`, serving nuget.org search and human legibility only; it is not validated by the build. Third-party packages SHALL be free to expose their verb in the `Dmon.Hosting` namespace following the first-party convention.

#### Scenario: Third-party package pins the contract
- **WHEN** a third-party package (e.g. `Acme.Dmon.Llama`) is authored
- **THEN** it references `Dmon.Abstractions@X.Y.*` and may publish on a version line independent of the first-party protocol-keyed lockstep

### Requirement: Restore-time incompatibility detection
An authored `Dmon.cs` SHALL pin its `#:package` set at `@<protocol>.*` across the first-party packages, and a protocol-incompatible combination SHALL fail at `dotnet restore`/build before any process starts (ADR-023 D5). The runtime `agentReady` `protocolVersion` skew gate (ADR-011 D6) SHALL survive only as a backstop for the prebuilt stock path, which is shipped already-resolved rather than restored by the author.

#### Scenario: Incompatible package set fails restore
- **WHEN** an authored `Dmon.cs` `#:package`s a set of first-party packages whose `Major.Minor` lines do not all match
- **THEN** `dotnet restore` fails to resolve a coherent graph and the core never starts

#### Scenario: Runtime gate backstops the prebuilt stock path
- **WHEN** the prebuilt stock default core (shipped pre-resolved) is launched
- **THEN** the `agentReady` `protocolVersion` gate remains in force as the skew backstop for that path, since it was not produced by an author's `dotnet restore`
