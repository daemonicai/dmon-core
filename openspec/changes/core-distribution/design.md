## Context

dmon ships as a working tree today: `make build` publishes `dmoncore`, `dmon`, and extensions into `build/`, and `Dmon.Terminal` locates the core via `CoreProcessManager.ResolveCorePath` (override → `DMON_CORE_PATH` → a published `dmoncore/dmoncore` sibling → dev `bin/` probes). Nothing is on nuget.org; the contract assemblies (`Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`) are `ProjectReference`s with no packaging metadata, and no project sets a `<Version>`.

Two facts shape this change:
- `dmon` and `dmoncore` are **separate processes** that talk over JSONL/stdio (ADR-003). Each carries its own compiled `Dmon.Protocol`; the binding contract is the **wire JSON shape**, which `Dmon.Protocol` defines on both ends. The `agentReady` event already carries `protocolVersion` (today hardcoded `"1.0"`) and `coreVersion`, but the host does not yet *check* either.
- The host and the core are expected to update on **different cadences** — the console host changes less often than the core. Welding their versions together (bundling, or a shared package reference) would force lockstep releases we do not want.

This change makes dmon distributable while preserving that independence: the host is acquired via `dotnet tool install`; the core is acquired on demand into the global NuGet cache; compatibility is governed by the wire-protocol version, not by a package dependency edge.

## Goals / Non-Goals

**Goals:**
- Publish the SDK trio (`Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`) and the `dmon` tool to nuget.org so dmon is installable and extensions are buildable out-of-tree.
- Acquire `dmoncore` at runtime into the global NuGet cache with no bundling and no package reference from `dmon`.
- Factor discovery/acquisition/lifecycle into a reusable internal `Dmon.Runtime` library.
- Encode host↔core compatibility in a 3-part protocol-keyed version scheme, enforced at the handshake.
- Keep `IDmonExtension` binary-compatible; no RPC/session/permission/auth changes.

**Non-Goals:**
- The Avalonia desktop host (V1 out-of-scope). `Dmon.Runtime` is factored to enable it later; no Avalonia code is written here.
- Self-contained / AOT / per-RID native distribution of the core. The core ships framework-dependent and is launched via `dotnet exec` (relies on the .NET 10 runtime a tool user already has).
- A private/internal feed, package signing beyond nuget.org defaults, or multi-feed publishing.
- Rehydrating conversation history across core restarts (already deferred elsewhere).

## Decisions

### D1 — Record the distribution model as an ADR
A new ADR records: granular contract packages on nuget.org; `dmon` acquires `dmoncore` at runtime rather than bundling it; the 3-part protocol-keyed version & compatibility scheme.
- **Why:** these are binding architectural decisions (ADRs are binding per CLAUDE.md). The "core is a runtime-acquired NuGet package, not a bundled payload" choice in particular is non-obvious and will be questioned without a recorded decision.

### D2 — `dmoncore` is published as a runnable publish closure, not a library
The `dmoncore` package carries its **full framework-dependent publish output** (every dependency DLL + `dmoncore.deps.json` + `dmoncore.runtimeconfig.json`), laid out so it can be run directly from the cache via `dotnet exec <pkg>/…/dmoncore.dll`.
- **Why:** a normal library package contains only `lib/<tfm>/dmoncore.dll` plus *declared* dependency metadata — not the resolved dependency graph, no deps.json/runtimeconfig. That is not runnable. Carrying the publish closure makes "fetch one package → run it" work with the low-level acquisition API and stays RID-agnostic.
- **Alternative:** keep `dmoncore` a plain library and have `dmon` perform a full transitive restore (`NuGet.Commands` `RestoreCommand`) + compose a runtimeconfig at runtime. Rejected — much more runtime machinery for no benefit.

### D3 — `dmon` does not bundle or package-reference `dmoncore`; it acquires on demand
`Dmon.Runtime` resolves the core in precedence order: `--core-path` → `DMON_CORE_PATH` → global NuGet cache (`SettingsUtility.GetGlobalPackagesFolder`) → on-demand fetch from nuget.org. The two override tiers remain the offline / air-gapped / dev escape hatch and take priority over the cache.
- **Why:** keeps `dmon`'s package graph clean and the cadences independent; the core is data the host fetches, not a compile-time dependency.

### D4 — Acquisition uses the NuGet client SDK (`NuGet.Protocol`)
Cache check via `GlobalPackagesFolderUtility.GetPackage`; version resolution via `FindPackageByIdResource.GetAllVersionsAsync`; download via `CopyNupkgToStreamAsync`; install into the global packages folder via `GlobalPackagesFolderUtility.AddPackageAsync`.
- **Why:** `AddPackageAsync` writes the correct cache layout (lowercased path, `.nupkg.sha512`/`.nupkg.metadata`) that the runtime expects; reimplementing those semantics over the hand-rolled flat-container HTTP path (as `ExtensionSourceFetcher` does for nuspec reads) would be fragile.
- **Alternative:** shell out to `dotnet`. Rejected — no first-class "download to cache" verb; `dotnet tool install` targets the tool store, not the package cache.

### D5 — 3-part protocol-keyed version scheme
Versions are `Major.Minor.Patch`. `Major.Minor` = the wire-protocol contract version; `Patch` = each component's independent release counter. First protocol = `0.1`. Compatibility = identical `Major.Minor`. `dmon` resolves the **newest `dmoncore` whose `Major.Minor` equals its own** (`[0.1.0, 0.2.0)`).
- **Why:** decouples cadences while making compatibility a trivial, total comparison. SemVer-native (3-part), so MinVer with per-project tag prefixes (`dmon-`, `core-`) drives independent release counters from this one repo; the human rule is that `Major.Minor` tracks the protocol.
- **Alternative:** 4-part `Major.Minor.Build.Revision`. Rejected — NuGet normalizes trailing-zero revisions (`0.1.0.0`→`0.1.0`), it is non-idiomatic, and it breaks MinVer. The 3rd part alone carries each component's counter; no 4th axis is needed.

### D6 — Single source of truth for the protocol version
A public constant `ProtocolVersion.Current` (value `"0.1"`) lives in `Dmon.Protocol`. `dmoncore` emits it at `agentReady` (replacing the hardcoded `"1.0"`). `Dmon.Runtime` uses it to (a) filter which `dmoncore` versions to acquire and (b) gate the handshake — comparing the core's reported `protocolVersion.Major.Minor` to its own and failing with a clear, actionable error on mismatch.
- **Why:** acquisition filter and runtime guard then key off the identical value, defined in the one assembly that defines the contract. The handshake gate covers the paths acquisition cannot (a stale cached core; a hand-set `DMON_CORE_PATH`).

### D7 — `Dmon.Runtime` is an internal, unpublished library
A new `src/Dmon.Runtime` (`IsPackable=false`) owns discovery, acquisition, process lifecycle (`CoreProcessManager` relocates here), and the compatibility gate. `Dmon.Terminal` takes a `ProjectReference` and is reduced to console concerns. Dependencies: `Dmon.Protocol` (for `ProtocolVersion` + `AgentReadyEvent`) and `NuGet.Protocol`.
- **Suggested seam (open):** `Dmon.Runtime` exposes a "start a protocol-compatible core" entry point returning the live stdio streams + the parsed `AgentReadyEvent`, so the compat check is centralized and both hosts inherit it; each host runs its own event loop afterward.
- **Why:** the desktop host (future) needs identical bootstrap; keeping it out of `Dmon.Terminal` avoids a later extraction. Because `dmon` packs as a tool, `Dmon.Runtime` and its `NuGet.Protocol` graph are included transitively in the tool package.

### D8 — Packaging metadata centralised; MPL-2.0
A `Directory.Build.props` carries shared metadata (Authors, RepositoryUrl, `PackageLicenseExpression=MPL-2.0`, SourceLink, `ContinuousIntegrationBuild`, deterministic builds, symbol packages). A `LICENSE` file is added at the repo root. `IsPackable` is set false by default and true only on the five published projects. Release is a tag-triggered `release.yml` (`dotnet pack` + `dotnet nuget push`) using a `NUGET_API_KEY` secret; the `Dmon.*` prefix is reserved on nuget.org.

## Risks / Trade-offs

- **[First-run requires network]** A fresh install must fetch `dmoncore` on first launch. → Show progress; fail with an actionable message naming `DMON_CORE_PATH`/`--core-path`; subsequent runs are offline (cache hit).
- **[Old `dmon` after a protocol bump]** When the protocol moves to `0.2`, a `dmon 0.1.*` install must keep resolving the last `0.1.*` core. → Never unlist old `dmoncore` lines; the `Major.Minor` filter naturally pins old hosts to their compatible core line.
- **[Stale cache / overridden core mismatch]** An override or stale cache could point at an incompatible core. → The D6 handshake gate catches it deterministically and errors clearly, rather than failing mid-turn.
- **[`NuGet.Protocol` dependency weight]** It pulls a sizeable dependency graph into `Dmon.Runtime` (and thus the `dmon` tool package). → Accepted; it is the correct client library and tool packages routinely carry their closure.
- **[Human discipline on `Major.Minor` = protocol]** Nothing mechanically forces a release tag's `Major.Minor` to match `ProtocolVersion.Current`. → Add a build/release check that fails if a packed version's `Major.Minor` diverges from `ProtocolVersion.Current`.

## Migration Plan

Additive; nothing existing changes behaviourally for working-tree users (the dev-layout discovery tiers remain).
1. Write and accept the distribution ADR (D1).
2. Add `Dmon.Protocol.ProtocolVersion` and emit it at `agentReady`; have nothing depend on the value yet.
3. Create `Dmon.Runtime`; move `CoreProcessManager`; add discovery + acquisition + the compat gate; repoint `Dmon.Terminal`.
4. Add packaging metadata (`Directory.Build.props`, `LICENSE`, `IsPackable`, MinVer tag prefixes); make the SDK trio, `dmon`, and `dmoncore` packable.
5. Add `release.yml`; reserve the `Dmon.*` prefix; publish.
No rollback concern: until packages exist on nuget.org and a tag is pushed, runtime behaviour is unchanged.

## Open Questions

- **SDK-package version line.** Do `Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions` share one version line (recommended: one coherent "SDK 0.x" keyed to `Dmon.Protocol`'s `Major.Minor`), or version independently? Default to a shared SDK line unless review decides otherwise. (Three version lines total: SDK trio, `dmon`, `dmoncore`.)
- **`Dmon.Runtime` public API seam** (D7) — exact shape of the "start a compatible core" entry point and how `/reload` (which calls `CoreProcessManager.RestartAsync`) maps onto it.
- **Version-skew guard mechanism** — where the `Major.Minor == ProtocolVersion.Current` check lives (MSBuild target vs release-workflow step).
