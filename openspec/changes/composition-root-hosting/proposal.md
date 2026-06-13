## Why

Three subsystems answer one question — *what code makes up this agent, and how does it get here?*: runtime core acquisition (a bespoke NuGet downloader), a config-driven active-extension list (reflection-loaded into the Default ALC), and a dynamic `.csx` / `extension.load` tier. ADR-019 (Accepted) replaces all three with one mechanism: the user authors a `Dmon.cs` composition root and the .NET 10 SDK builds it. This is a large net subtraction that also removes the design's riskiest machinery — runtime loading of untrusted code into the agent's own process.

## What Changes

- **`dmoncore` becomes a framework library.** It exposes a hosting surface — `DmonHost.CreateBuilder(args)` → builder (provider/model, extensions, permission mode, profile) → `.Build().RunAsync()`, where `RunAsync()` *is* today's JSONL/stdio core loop. The wire contract (ADR-003), session storage (ADR-004), and the `IChatClient` permission pipeline (ADR-002/006) are unchanged — only the **entry point** moves out of the package.
- **`Dmon.cs` is the composition root** — a file-based program with `#:package` directives + builder calls. Extensions become **compile-time dependencies**, not runtime loads.
- **BREAKING — the entire dynamic-load tier is removed:** no runtime `extension.load`, no `.csx` tier / `Dotnet.Script.Core`, no config-driven active-extension list, no reflection discovery into the Default ALC, no `promote` path, no runtime NuGet downloader.
- **The host builds, then runs off the wire.** It runs `dotnet build Dmon.cs` as a separate captured step (also the incremental staleness gate), then launches **`dotnet run Dmon.cs --no-build`** as the stdio child via the existing `ICoreLauncher`/`ICoreProcess` seam — so MSBuild/restore output never touches the JSONL stdout channel.
- **Launcher precedence:** `./Dmon.cs` > `$DMON_CORE` > a built-in default. The default is a **prebuilt** publish of a canonical `Dmon.cs` that references the `dmoncore` library, so an empty dir / no-SDK first run works on the runtime alone. `dmon init` scaffolds an editable `Dmon.cs`.
- **`config.yaml` is retained for *settings only*, not extension lists**, and is exposed through the builder (code may read or override it).
- **Acquisition is `dotnet restore` over the `#:package` set.** ADR-011 protocol-keyed versioning survives: `#:package dmoncore@<protocol>.*` plus the `agentReady` `protocolVersion` gate.
- **`/reload`** rebuilds (incremental) and re-runs `Dmon.cs` — the same restart-between-turns boundary.

## Capabilities

### New Capabilities
- `composition-root-hosting`: the `DmonHost` builder/`RunAsync` surface, the `Dmon.cs` file-based composition root, the build-then-`--no-build`-run launch that keeps the build off the JSONL wire, launcher precedence (`Dmon.cs` > `$DMON_CORE` > prebuilt default), and `dmon init` scaffolding.

### Modified Capabilities
- `extension-model`: extensions are **compile-time** `#:package` + builder registrations; the `.csx` tier, the config-declared active-extension set (user/project union, load-at-startup, fail-soft), and reflection-based middleware discovery are removed/replaced by builder wiring. The `IDaemonExtension`/`AIFunction` contract and the one-shared-context principle are retained.
- `core-runtime-acquisition`: discovery precedence updated to `Dmon.cs` > `$DMON_CORE` > prebuilt default; the on-demand runtime NuGet downloader is removed (acquisition is SDK `dotnet restore` over `#:package`); the core is launched via build-then-`--no-build`-run; the protocol-compatibility handshake gate is retained.
- `package-publishing`: `dmoncore` is published as a **library** package; the runnable publish closure survives only as the **prebuilt default core** (a canonical `Dmon.cs` built ahead of time), not as the unit of distribution. Granular contract packages and the protocol-keyed three-part version scheme are retained.

## Impact

- **Code:** `dmoncore` gains `Dmon.Hosting` (`DmonHost`/builder); the core entry point moves to `Dmon.cs`. `Dmon.Runtime`'s `ICoreLauncher`/`ICoreProcess` learns build-then-`--no-build`-run + the new precedence. **Deleted:** `Dotnet.Script.Core` usage and the `.csx` tier, the config-driven extension loader + reflection middleware discovery, the runtime NuGet downloader, the `promote` path.
- **Config:** `config.yaml` loses the active-extension list (settings only); `dmon init` scaffolds `Dmon.cs`.
- **Packaging:** `dmoncore` library package + a prebuilt canonical-`Dmon.cs` default core artifact.
- **ADRs:** realizes ADR-019 (supersedes ADR-009 in full, ADR-011 D2–4, ADR-008's dynamic-load mechanism, ADR-002's `.csx`/`promote`). Unchanged: ADR-003 wire, ADR-004 storage, ADR-002/006 permission pipeline.
- **Out of scope (follow-up changes):** agent definitions (ADR-020) and the **`compose` permission tier** (ADR-021) — the gate on the agent editing its own `Dmon.cs` is *not* built here and must land before the agent is allowed to self-modify composition. New-SDK dependency for the `Dmon.cs` path (the prebuilt default preserves the runtime-only experience).
