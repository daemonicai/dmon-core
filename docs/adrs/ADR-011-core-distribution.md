# ADR-011: Distribution Model — Packages, Runtime Core Acquisition, Protocol-Keyed Versioning

**Date:** 2026-05-30
**Status:** Accepted

## Context

dmon ships only as a working tree today: `make build` publishes `dmoncore`, `dmon`, and extensions into `build/`, and `Dmon.Terminal` locates the core via `CoreProcessManager.ResolveCorePath`. Nothing is on nuget.org, and the contract assemblies (`Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`) are `ProjectReference`s with no packaging metadata. An out-of-tree extension author has nothing to `dotnet add package` against; the just-accepted `sub-agent-extensions` change (ADR-010) deliberately unblocks a `dmon-websearch` extension that today has no compilable contract to depend on.

Two facts constrain how dmon is made distributable:

- `dmon` (the host) and `dmoncore` (the agent core) are **separate processes** that talk over JSONL/stdio (ADR-003). Each carries its own compiled `Dmon.Protocol`; the binding contract is the **wire JSON shape**, not a shared assembly reference. The `agentReady` event already carries `protocolVersion` and `coreVersion`, but the host does not yet check either.
- The host and the core are expected to release on **different cadences** — the console host changes less often than the core. Welding their versions together (bundling, or a shared package-reference edge) would force lockstep releases we do not want.

This ADR records how dmon becomes installable and extensible while preserving that process- and cadence-independence.

## Decision

1. **Granular contract packages on nuget.org.** `Dmon.Protocol`, `Dmon.Abstractions`, and `Dmon.Extensions` are published as three separate packages so out-of-tree authors can compile against the contract. The inter-package edges (`Dmon.Extensions` → `Dmon.Protocol`, `Dmon.Abstractions` → `Dmon.Protocol`) are declared as **package dependencies**, not embedded assemblies. The three share **one SDK version line** keyed to `Dmon.Protocol`'s `Major.Minor`.

2. **`dmon` ships as a `dotnet` global tool** (`Dmon.Terminal`, `PackAsTool`, command name `dmon`). It **does not bundle and does not package-reference `dmoncore`.** The core is data the host fetches at runtime, not a compile-time dependency — this is what keeps the cadences independent.

3. **`dmon` acquires `dmoncore` at runtime into the global NuGet cache.** Discovery precedence is: `--core-path` override → `DMON_CORE_PATH` env var → `dmoncore` present in the global NuGet cache → on-demand fetch from nuget.org. The two override tiers take priority over the cache and remain the offline / air-gapped / dev escape hatch. Acquisition uses the NuGet client SDK (`NuGet.Protocol`): version listing via `FindPackageByIdResource`, download via `CopyNupkgToStreamAsync`, install via `GlobalPackagesFolderUtility.AddPackageAsync` (which writes the cache layout the runtime expects).

4. **`dmoncore` is published as a runnable framework-dependent publish closure, not a plain library.** The package carries every dependency assembly plus `dmoncore.deps.json` and `dmoncore.runtimeconfig.json`, laid out so the package — once in the cache — runs directly via `dotnet exec <pkg>/…/dmoncore.dll` with no further restore. A normal `lib/<tfm>` library package is not runnable (no resolved dependency graph, no deps.json/runtimeconfig); the closure makes "fetch one package → run it" work and stays RID-agnostic (the core is framework-dependent and relies on the .NET 10 runtime the tool user already has).

5. **3-part protocol-keyed version scheme.** Versions are `Major.Minor.Patch`. `Major.Minor` **equals the wire-protocol contract version**; `Patch` is each component's independent release counter. The first protocol is `0.1`. Compatibility is an identical `Major.Minor`. `dmon` resolves the **newest `dmoncore` whose `Major.Minor` equals its own** (e.g. `[0.1.0, 0.2.0)`). MinVer with per-project tag prefixes (`dmon-`, `core-`, and the shared SDK prefix) drives the independent `Patch` counters from this one repository; the human rule is that `Major.Minor` tracks the protocol.

6. **Single source of truth for the protocol version.** A public `Dmon.Protocol.ProtocolVersion.Current` constant (`"0.1"`) is the one definition of the wire-protocol version. `dmoncore` emits it at `agentReady` (replacing the previously hardcoded `"1.0"`). The runtime uses the identical value to (a) filter which `dmoncore` versions it will acquire and (b) gate the handshake — comparing the core's reported `protocolVersion.Major.Minor` to its own and failing with a clear, actionable error on mismatch. The handshake gate covers the paths acquisition cannot: a stale cached core, or a hand-set `DMON_CORE_PATH`.

7. **Discovery, acquisition, lifecycle, and the compatibility gate live in an internal `Dmon.Runtime` library** (`IsPackable=false`). `CoreProcessManager` relocates here from `Dmon.Terminal`; `Dmon.Terminal` takes a `ProjectReference` and is reduced to console concerns. `Dmon.Runtime` carries no console/TUI dependency so a future desktop host inherits identical bootstrap. Because `dmon` packs as a tool, `Dmon.Runtime` and its `NuGet.Protocol` graph ride into the tool package transitively.

8. **Version-skew guard as an MSBuild target.** A target (in `Directory.Build.props`/targets) fails any pack whose `Major.Minor` diverges from `ProtocolVersion.Current`, so the protocol-keying rule is enforced on every local and CI pack, not only in the release job.

9. **Packaging metadata centralised; MPL-2.0.** A `Directory.Build.props` carries shared metadata (Authors, RepositoryUrl, `PackageLicenseExpression=MPL-2.0`, SourceLink, deterministic / `ContinuousIntegrationBuild`, symbol packages). A `LICENSE` file sits at the repo root. `IsPackable` defaults to false and is true only on the five published projects (`Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`, `Dmon.Terminal`, `Dmon.Core`). Release is a tag-triggered `release.yml` (`dotnet pack` + `dotnet nuget push`) using a `NUGET_API_KEY` secret; PR CI never publishes. The `Dmon.*` ID prefix is reserved on nuget.org.

## Consequences

- **dmon becomes installable** via `dotnet tool install` and extensions become buildable out-of-tree against the published contract packages — unblocking `dmon-websearch` (ADR-010) concretely, not just in principle.
- **Cadences stay independent.** Because compatibility is governed by the wire-protocol `Major.Minor` rather than a package-dependency edge, `dmon` and `dmoncore` release on their own counters; a protocol bump to `0.2` is the only event that breaks an old host, and old `dmoncore 0.1.*` lines must never be unlisted so a `dmon 0.1.*` install keeps resolving its compatible core.
- **First run requires network.** A fresh install fetches `dmoncore` on first launch; failures surface an actionable error naming `--core-path` / `DMON_CORE_PATH`, and subsequent runs are offline cache hits.
- **`NuGet.Protocol`'s dependency graph rides into the `dmon` tool package.** Accepted: it is the correct client library and tool packages routinely carry their closure.
- **Human discipline plus a mechanical guard** keep `Major.Minor` == protocol: the MSBuild target (Decision 8) fails the pack if they diverge.
- **No change** to RPC message shapes, session storage, the permission model, or provider auth; `IDmonExtension` stays binary-compatible.

## Alternatives

- **Bundle `dmoncore` inside the `dmon` tool, or have `dmon` package-reference it.** Rejected — both weld the two release cadences together, the thing this model exists to avoid.
- **Publish `dmoncore` as a plain library and have `dmon` perform a full transitive restore + compose a `runtimeconfig` at runtime.** Rejected — far more runtime machinery (`NuGet.Commands` `RestoreCommand`) for no benefit over carrying the publish closure.
- **Shell out to `dotnet` to acquire the core.** Rejected — there is no first-class "download to the package cache" verb; `dotnet tool install` targets the tool store, not the package cache.
- **4-part `Major.Minor.Build.Revision` version.** Rejected — NuGet normalizes trailing-zero revisions, it is non-idiomatic, and it breaks MinVer; the 3rd part alone carries each component's counter.
- **Version the three SDK packages independently.** Rejected for V1 in favour of one coherent SDK line keyed to `Dmon.Protocol` — simpler for consumers and fewer mismatch modes.

## Relationship to other ADRs

- **ADR-001** — no MAF dependency is introduced; packaging is orthogonal to the LLM abstraction.
- **ADR-002 / ADR-008** — the published `Dmon.Abstractions` / `Dmon.Extensions` packages are exactly the extension contract these ADRs define; the loading mechanism is unchanged.
- **ADR-003** — the wire protocol is the binding host↔core contract; this ADR makes its version (`ProtocolVersion.Current`) explicit and gates the `agentReady` handshake on it. No RPC message shapes change.
- **ADR-005** — provider auth is untouched; the only new credential is the CI-side `NUGET_API_KEY` used to publish, never committed.
- **ADR-007** — the provider-extension lifecycle is unaffected; `dmoncore`'s publish closure simply includes provider extensions as before.
- **ADR-010** — the contract packages this ADR publishes are what make an out-of-tree `dmon-websearch` compilable; this ADR is the distribution half of that unblock.
