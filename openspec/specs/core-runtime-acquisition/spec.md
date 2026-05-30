# core-runtime-acquisition Specification

## Purpose
TBD - created by archiving change core-distribution. Update Purpose after archive.
## Requirements
### Requirement: Core discovery precedence
`Dmon.Runtime` SHALL resolve the `dmoncore` executable in a fixed precedence order: (1) an explicit `--core-path` override, (2) the `DMON_CORE_PATH` environment variable, (3) the `dmoncore` package present in the global NuGet cache, (4) on-demand acquisition from nuget.org. The two override tiers SHALL take priority over the cache so they remain a working offline/dev escape hatch.

#### Scenario: Override wins over the cache
- **WHEN** `--core-path` (or `DMON_CORE_PATH`) points at an existing core and a cached `dmoncore` is also present
- **THEN** `Dmon.Runtime` uses the override-specified core and does not consult the cache or the network

#### Scenario: Cache hit avoids the network
- **WHEN** no override is set and a compatible `dmoncore` is already in the global NuGet cache
- **THEN** `Dmon.Runtime` uses the cached core and performs no network request

### Requirement: On-demand acquisition into the global NuGet cache
When no override is set and no compatible `dmoncore` is cached, `Dmon.Runtime` SHALL download the `dmoncore` package from nuget.org and install it into the global NuGet packages folder (resolved via the NuGet settings, honouring `NUGET_PACKAGES`/`nuget.config`), writing the standard cache layout so the package is subsequently discoverable as a cache hit.

#### Scenario: First run fetches and caches the core
- **WHEN** `Dmon.Runtime` starts with no override and an empty cache for `dmoncore`
- **THEN** it downloads the package and installs it into the global packages folder, and a subsequent start resolves it as a cache hit with no network request

#### Scenario: Acquisition failure surfaces an actionable error
- **WHEN** acquisition is required but the network is unavailable
- **THEN** `Dmon.Runtime` fails with an actionable error that names the `--core-path` / `DMON_CORE_PATH` overrides as the offline workaround

### Requirement: Version resolution by protocol compatibility
When resolving which `dmoncore` to acquire, `Dmon.Runtime` SHALL select the newest `dmoncore` version whose `Major.Minor` equals its own `ProtocolVersion.Current` `Major.Minor`, and SHALL NOT select a `dmoncore` whose `Major.Minor` differs.

#### Scenario: Newest compatible core is chosen
- **WHEN** the host's protocol is `0.1` and nuget.org offers `dmoncore` `0.1.3`, `0.1.9`, and `0.2.0`
- **THEN** `Dmon.Runtime` resolves `0.1.9` and never `0.2.0`

### Requirement: Launch the core from its publish closure
`Dmon.Runtime` SHALL launch the resolved `dmoncore` from its cached publish closure via `dotnet exec` against the package's `dmoncore.dll`, relying on the bundled `deps.json`/`runtimeconfig.json` to resolve dependencies, and connect to it over JSONL/stdio.

#### Scenario: Cached core is executed
- **WHEN** a compatible `dmoncore` is resolved in the cache
- **THEN** `Dmon.Runtime` starts it via `dotnet exec` of the cached `dmoncore.dll` and exposes its stdio for the host's RPC loop

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

