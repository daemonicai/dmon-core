## Why

dmon cannot be installed or extended by anyone outside this repository. The host is only runnable from a `make build` working tree, and extension authors have nothing to `dotnet add package` against — the contract assemblies are project references, not packages. The just-merged `sub-agent-extensions` change exists to unblock an out-of-tree `dmon-websearch` extension that, today, has no compilable contract to depend on. This change makes dmon distributable: `dmon` as a `dotnet` global tool, and the contract assemblies as NuGet packages — while keeping the host process (`dmon`) and the agent core (`dmoncore`) on independent release cadences bridged only by the wire protocol.

## What Changes

- Publish three **granular** contract packages to nuget.org so extension authors can build against them: `Dmon.Protocol`, `Dmon.Abstractions`, `Dmon.Extensions`.
- Publish **`dmon`** as a `dotnet` global tool (`Dmon.Terminal`, `PackAsTool`, command name `dmon`).
- Publish **`dmoncore`** as a NuGet package that carries its **full framework-dependent publish closure** (all dependency DLLs + `deps.json` + `runtimeconfig.json`), so it is runnable directly from the package via `dotnet exec`.
- The `dmon` tool **does not bundle and does not package-reference `dmoncore`.** At startup it discovers the core (override → env → global NuGet cache → on-demand fetch from nuget.org) and launches it.
- Add a new **internal** library `Dmon.Runtime` (not published; referenced by `Dmon.Terminal` now, reusable by a future desktop host) that owns core **discovery**, **acquisition** into the global NuGet cache, **process lifecycle** (relocating `CoreProcessManager` out of `Dmon.Terminal`), and the **protocol-version compatibility gate**.
- Adopt a **3-part version scheme**: `Major.Minor` tracks the wire-protocol contract, `Patch` is each component's own release counter. First protocol is `0.1`. `dmon 0.1.*` interoperates with any `dmoncore 0.1.*`; `dmon` resolves the newest `dmoncore` matching its own `Major.Minor`.
- Make the wire protocol version a single source of truth: a `ProtocolVersion` constant in `Dmon.Protocol`, emitted at the `agentReady` handshake (replacing the hardcoded `"1.0"`) and used by `Dmon.Runtime` to filter acquisition **and** gate the handshake.
- Add a tag-driven **release pipeline** (publish to nuget.org) and package metadata (MPL-2.0 license + `LICENSE` file, SourceLink, symbols, deterministic CI builds). Reserve the `Dmon.*` ID prefix.

No change to the RPC message shapes, session storage, permission model, or provider auth. No breaking change to `IDmonExtension`. Avalonia desktop host remains **out of scope** — `Dmon.Runtime` is factored to enable it later, not built against it now.

## Capabilities

### New Capabilities
- `package-publishing`: Which assemblies are packable and which are not, package metadata and license (MPL-2.0), the granular SDK trio + the `dmon` tool + the `dmoncore` runnable package, the 3-part protocol-keyed version scheme, and the tag-driven nuget.org release pipeline.
- `core-runtime-acquisition`: The `Dmon.Runtime` library — `dmoncore` discovery precedence, on-demand acquisition into the global NuGet cache, launching the core from its cached publish closure via `dotnet exec`, and the protocol-version compatibility gate at the `agentReady` handshake.

### Modified Capabilities
<!-- None. Host capabilities (terminal-host, console-host) are consumers of Dmon.Runtime; their spec-level behaviour (spawn core, /reload re-binds stdio) is unchanged. CoreProcessManager relocating to Dmon.Runtime is an implementation detail, not a requirement change. -->

## Impact

- **New ADR** in `docs/adrs/` (next free number) recording the distribution model: granular contract packages on nuget.org; `dmon` acquires `dmoncore` at runtime rather than bundling it; the 3-part protocol-keyed version/compatibility scheme.
- **New project** `src/Dmon.Runtime/` (`IsPackable=false`); `CoreProcessManager` moves here from `Dmon.Terminal`; new acquisition code depends on `NuGet.Protocol` and `Dmon.Protocol`.
- **`Dmon.Terminal`** gains `PackAsTool`/tool metadata and a `ProjectReference` to `Dmon.Runtime`; reduced to console concerns. `NuGet.Protocol`'s dependency graph rides transitively into the `dmon` tool package.
- **`Dmon.Protocol`** gains a public `ProtocolVersion` constant; `RpcHostedService` emits it at `agentReady` (replacing `"1.0"`).
- **Packaging metadata**: `Directory.Build.props` (shared authors/repo/license/SourceLink/deterministic), per-package README, `IsPackable` flags, MinVer with per-project tag prefixes (`dmon-`, `core-`) for independent cadence.
- **CI/CD**: a tag-triggered `release.yml` running `dotnet pack` + `dotnet nuget push` with a `NUGET_API_KEY` secret; `Dmon.*` prefix reservation on nuget.org.
- **Coordinates with**: the just-merged `sub-agent-extensions` (its `Dmon.Abstractions`/`Dmon.Extensions` surface is now what we publish) and the in-flight `extension-middleware-tier` (which adds types to the SDK contract and takes the next SDK `Patch` bump after this ships).
- **Downstream**: unblocks installing dmon via `dotnet tool install` and building out-of-tree extensions (e.g. `dmon-websearch`) against published contract packages.
