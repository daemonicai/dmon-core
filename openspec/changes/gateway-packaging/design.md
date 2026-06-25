## Context

`frontends/Dmon.Gateway` is the WebSocket gateway host (ADR-012, `remote-session-gateway` spec). It is built today only as a plain console executable (`OutputType=Exe`, net10.0) — there is no pack step and no install step. `dmonium`'s `GatewayManager.swift` resolves the Gateway binary with this precedence (lines ~79–84):

1. an explicit in-app override,
2. `DMON_GATEWAY_PATH` (env or `~/.dmon/config.yaml`),
3. the default `~/.dotnet/tools/Dmon.Gateway`.

The default (3) is the canonical install location of a .NET global tool. Because `Dmon.Gateway` is not packaged as a tool, that path is never created, so on a clean machine the Gateway never resolves and the dmonium Gateway row is red until the user manually points `DMON_GATEWAY_PATH` at a hand-built binary.

Two binding ADRs frame the fix:
- **ADR-024 / ADR-028:** the Gateway daemon and the dmonium `.app` are **app artifacts, independently versioned**, explicitly **not** on the protocol-lockstep `Dmon.*` NuGet release train.
- **ADR-011 / ADR-025:** distribution model and monorepo release families (NuGet vs app artifacts).

The `package-publishing` standing spec currently (a) lists the packable projects as the protocol-keyed first-party set and internal-libraries-not-packable, and (b) requires every *published* package version's `Major.Minor` to equal `ProtocolVersion.Current` (rejecting divergent versions). Making the Gateway a published tool package intersects both.

## Goals / Non-Goals

**Goals:**
- The Gateway is installable with one command and lands at `~/.dotnet/tools/Dmon.Gateway`, so dmonium's existing default resolution finds it and the Gateway row is green out of the box.
- A `make gateway` target parallel to `make daemon-app`.
- Keep the Gateway an independently-versioned app artifact (ADR-024) — exempt from the protocol-keyed lockstep version gate.
- Align the `package-publishing` spec with the above without contradicting any binding ADR.

**Non-Goals:**
- No change to `GatewayManager.swift` resolution logic (it already supports the default path) — beyond confirming casing matches.
- No change to the Gateway's runtime behaviour, the wire protocol, or `remote-session-gateway` semantics.
- No publishing of the Gateway tool to nuget.org in this change (local `dotnet tool install` from the packed `.nupkg` is the first cut; a release-pipeline push is a possible follow-on).
- No bundling of the Gateway into the dmonium `.app`, and no code-signing/notarisation.
- No change to the first-party protocol-keyed NuGet set or its release train.

## Decisions

### D1: `Dmon.Gateway` becomes a `PackAsTool` .NET global tool
Add to `Dmon.Gateway.csproj`: `PackAsTool=true`, `ToolCommandName` resolving the installed executable to **`Dmon.Gateway`** (matching `GatewayManager`'s default candidate exactly, including casing), a `PackageId`, and `IsPackable=true` (it currently inherits the repo default of false). `dotnet pack` then yields a tool package; `dotnet tool install` (global) places `~/.dotnet/tools/Dmon.Gateway`.

- **Why this over alternatives:** the default candidate `GatewayManager` already hard-codes is the dotnet-tools path; honoring it needs no Swift change and matches how `dmon` (`Dmon.Terminal`) is already shipped (`package-publishing` "`dmon` published as a dotnet tool"). 
- **Alternative — `make gateway` builds to a fixed local path + bless `DMON_GATEWAY_PATH`:** rejected as the *primary* mechanism; it leaves every fresh install needing a config edit and doesn't match the documented default. (`DMON_GATEWAY_PATH` remains supported as the override.)
- **Casing caveat (architect to confirm at apply):** dotnet tool command names are conventionally lowercase; the resolved file name must be exactly `Dmon.Gateway` to match the Swift default. If `ToolCommandName=Dmon.Gateway` does not produce that exact filename on macOS, the fallback is to adjust the default candidate in `GatewayManager` to the actual installed name — but prefer making the package match the existing expectation.

### D2: Independent versioning — exempt from the protocol-keyed gate
The Gateway tool package versions on its **own cadence** (ADR-024), not the protocol-keyed `Major.Minor` line. The `package-publishing` "Protocol-keyed three-part version scheme" requirement currently would reject any published package whose `Major.Minor ≠ ProtocolVersion.Current`; this change carves out **app-artifact dotnet tools** (the Gateway) from that gate. The Gateway is likewise distinguished from the protocol-keyed first-party NuGet set in the "packable projects" requirement: it is packable, but as an app artifact, not a protocol package.

- **Why:** directly upholds ADR-024 ("services and the menu bar app are app artifacts, independently versioned; ADR-024's protocol-lockstep package set is unchanged"). Forcing the Gateway onto the protocol line would contradict ADR-024.

### D3: `make gateway` target
Add a `gateway` target to the `Makefile` that builds, packs, and installs the tool from the local pack output (e.g. `dotnet pack -c Release frontends/Dmon.Gateway` then `dotnet tool install --global --add-source <pack-out> Dmon.Gateway`, or `--tool-path`), mirroring `make daemon-app`. Idempotent re-install (update if already installed).

- **Open at apply:** global `dotnet tool install` vs `--tool-path ~/.dotnet/tools`; and update-vs-install when already present. Implementation detail for the worker; the requirement is "`make gateway` produces a Gateway resolvable at the default path."

### D4: Docs
Note in `daemon/Daemon.App/README.md` (and optionally a `frontends/Dmon.Gateway/README.md`) that the Gateway is installed via `make gateway` / `dotnet tool install`, resolves at `~/.dotnet/tools/Dmon.Gateway`, and is overridable with `DMON_GATEWAY_PATH`.

## Risks / Trade-offs

- **[`ToolCommandName` casing mismatch with the Swift default]** → D1 caveat: confirm the installed filename is exactly `Dmon.Gateway`; if not, the minimal fallback is to align `GatewayManager`'s default candidate (a one-line Swift change) — but the goal is zero Swift change.
- **[Spec edit to a protocol-keyed invariant looks like an ADR change]** → D2: this *aligns* `package-publishing` with ADR-024 (which already exempts app artifacts), it does not contradict a numbered ADR decision; no superseding ADR expected. Flag as stop-and-ask if a reviewer judges otherwise.
- **[Version-consistency / packability CI check rejects the Gateway tool]** → the carve-out must be reflected in whatever build/release check enforces the protocol-keyed gate, or the Gateway pack will fail CI. Tasks must update that check alongside the csproj.
- **[Scope creep into nuget.org publishing]** → Non-Goal: this change stops at local install + a packable, independently-versioned tool; a release-pipeline push is a separate follow-on.
- **[`~/.dotnet/tools` not on a fresh machine's PATH]** → only affects invoking `Dmon.Gateway` from a shell; dmonium resolves it by absolute path, so the Gateway row works regardless. Note it in docs.

## Open Questions

- Should `make gateway` use a global `dotnet tool install` or an explicit `--tool-path ~/.dotnet/tools`? (Both land the same default path; global is the convention.)
- Should this change also wire the Gateway tool into the tag-driven release pipeline (nuget.org push), or leave that as a follow-on? (Leaning follow-on — see Non-Goals.)
