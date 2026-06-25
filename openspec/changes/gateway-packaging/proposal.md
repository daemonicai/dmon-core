## Why

`dmonium` (`daemon/Daemon.App`) supervises the Gateway, resolving its binary in `GatewayManager.swift` by precedence: an explicit override → the `DMON_GATEWAY_PATH` env/config value → the default `~/.dotnet/tools/Dmon.Gateway`. But **nothing in the repo ever produces that default**: `frontends/Dmon.Gateway/Dmon.Gateway.csproj` is a plain `OutputType=Exe` (net10.0) with no `PackAsTool`/`ToolCommandName`, and there is no `make gateway` target (unlike `make daemon-app`). On a fresh machine the default candidate never resolves, `GatewayManager.start()` no-ops, and the **Gateway health row sits red out of the box** — the one component a supervisor most needs working is the one that can't start without manual, undocumented setup.

This was found as a follow-up during `dmonium-windowed-dashboard` (now merged + archived); it was deliberately out of scope there.

## What Changes

- **`Dmon.Gateway` becomes an installable .NET global tool.** `Dmon.Gateway.csproj` gains `PackAsTool` + a `ToolCommandName` so `dotnet pack` produces a tool package and `dotnet tool install` lands the executable at `~/.dotnet/tools/Dmon.Gateway` — exactly the default candidate `GatewayManager` already looks for. No change to `GatewayManager`'s resolution logic is required.
- **A `make gateway` target** (build → pack → install the tool locally) parallel to the existing `make daemon-app`, so a developer/first-run can produce the Gateway with one command.
- **The Gateway tool is an app artifact, independently versioned — NOT on the protocol-lockstep package train** (ADR-024/ADR-028): its version moves on its own cadence like the dmonium `.app`, and it is exempt from the protocol-keyed `Major.Minor` version gate that governs the first-party NuGet set.
- **Docs:** the Daemon.App README (and/or a short Gateway README) note that the Gateway is installed via `make gateway` / `dotnet tool install`, and that `DMON_GATEWAY_PATH` overrides the resolved binary.

No change to dmon-core, the wire protocol, the Gateway's runtime behaviour, or `daemon/Daemon.App`'s Swift resolution logic. This is a packaging/build/distribution change confined to `frontends/Dmon.Gateway`, the build system, the `package-publishing` spec, and docs.

## Capabilities

### New Capabilities

_(none — the Gateway already exists; this change makes it installable/discoverable.)_

### Modified Capabilities

- `package-publishing`:
  - **ADD** a requirement that `Dmon.Gateway` is published as a .NET global tool (`PackAsTool` + `ToolCommandName`, installable via `dotnet tool install`, resolving to `~/.dotnet/tools/Dmon.Gateway`).
  - **MODIFY** "Only the five distribution projects are packable" — the Gateway is also packable, but as an **app-artifact tool**, distinct from the protocol-keyed first-party NuGet set.
  - **MODIFY** "Protocol-keyed three-part version scheme" — carve out app-artifact dotnet tools (the Gateway) from the protocol-lockstep `Major.Minor` gate; they version independently per ADR-024.

## Impact

- **Code / build (no runtime behaviour change):**
  - `frontends/Dmon.Gateway/Dmon.Gateway.csproj`: add `PackAsTool`, `ToolCommandName` (resolving to `Dmon.Gateway`), `PackageId`, and the metadata a packable project needs; flip `IsPackable` on for this project.
  - `Makefile`: new `gateway` target (build/pack/install) mirroring `daemon-app`.
  - Any version-consistency / packability check must treat the Gateway tool as an independently-versioned app artifact, not a protocol-keyed package.
- **dmonium:** none — `GatewayManager` already resolves `~/.dotnet/tools/Dmon.Gateway`; this change merely makes that path exist. The Gateway row turns green out of the box once the tool is installed.
- **Docs/specs:** `package-publishing` spec deltas (above); README note on installing the Gateway and `DMON_GATEWAY_PATH`.
- **No change** to dmon-core / protocol / the Gateway's behaviour / `Everything.slnx` membership / the first-party NuGet release train.
- **ADRs:** honors ADR-024 (app artifacts independently versioned, off the protocol train) and ADR-011/ADR-025 distribution decisions. No new or superseding ADR expected (this aligns the spec with ADR-024); if the architect/reviewer judges otherwise at apply time, that is a stop-and-ask.
