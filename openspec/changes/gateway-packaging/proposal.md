## Why

Two related problems with the WebSocket remote-access host:

1. **It is unreachable out of the box.** `dmonium` (`daemon/Daemon.App`) resolves the host binary in `GatewayManager.swift` by precedence: an explicit override → `DMON_GATEWAY_PATH` → the default `~/.dotnet/tools/Dmon.Gateway`. But **nothing produces that default**: `frontends/Dmon.Gateway/Dmon.Gateway.csproj` is a plain `OutputType=Exe` (net10.0) with no `PackAsTool`/`ToolCommandName`, and there is no `make gateway` target. On a fresh machine the default candidate never resolves, `start()` no-ops, and the health row sits red. (Found as a follow-up during `dmonium-windowed-dashboard`, now merged + archived.)

2. **The "gateway" name is being retired.** The host is renamed **`Dmon.Network`**, shipping as a .NET global tool whose command is **`ndmon`** (cf. `ncat`). The `Gateway`/`gateway` terminology is dropped across the project, the dmonium control surface, the standing capability prose, the docs, and the relevant ADRs.

Both land together because the packaging work and the rename touch the same project files; doing them in one pass avoids editing `Dmon.Gateway.csproj`, the solutions, and dmonium twice.

## What Changes

**Packaging (the OOTB fix):**
- `Dmon.Network` (renamed, see below) becomes an installable .NET global tool: `PackAsTool` + `ToolCommandName` = **`ndmon`**, so `dotnet tool install` lands `~/.dotnet/tools/ndmon`.
- A `make network` target (build → pack → install) parallel to `make daemon-app`.
- The tool is an **app artifact, independently versioned — NOT on the protocol-lockstep package train** (ADR-024): exempt from the protocol-keyed `Major.Minor` version gate.

**Rename / rebrand (full):**
- **Project:** `frontends/Dmon.Gateway` → `frontends/Dmon.Network`; csproj, `RootNamespace`/`AssemblyName`/`PackageId`, and `namespace Dmon.Gateway` → `Dmon.Network` (all ~30 sources); every `Gateway*` type → `Network*` (e.g. `GatewayOptions`→`NetworkOptions`, `IGatewayConnection`→`INetworkConnection`, `WebSocketGatewayConnection`→`WebSocketNetworkConnection`, `GatewayBindPolicy`→`NetworkBindPolicy`, `GatewayConnectionEndpoint`→`NetworkConnectionEndpoint`, the `Gateway*Paths`, etc.).
- **Tests:** `test/Dmon.Gateway.Tests` → `test/Dmon.Network.Tests` (dir, csproj, namespaces, references).
- **Solutions / references:** `frontends.slnx`, `Everything.slnx`, and any `ProjectReference` updated to the new paths/names.
- **dmonium (`daemon/Daemon.App`):** `GatewayManager` → `NetworkManager` (file + class), the health component label `"Gateway"` → `"Network"`, `observeGatewayStopped` → `observeNetworkStopped`, the Settings "Gateway binary path" field, the default resolved path → `~/.dotnet/tools/ndmon`, and the config/env key `DMON_GATEWAY_PATH` → `DMON_NETWORK_PATH` (clean break — no prod deployments, no migration).
- **Standing spec:** `remote-session-gateway` terminology updated ("the gateway" → "the network host"; the "Gateway session-create control frame" requirement renamed). The capability **id/folder stays `remote-session-gateway`** for now (internal identifier; renaming it is high cross-reference churn — flagged as an open question).
- **ADRs:** a new **ADR-033** records the rename and amends the "Gateway" terminology in ADR-012 (remote-session-transport), ADR-017 (iOS embedded Tailscale), ADR-018 (per-device keys), and ADR-028 (frontends list). No prior ADR *decision* is reversed — only terminology — so this is a recording/terminology ADR, not a substantive supersession (reviewer confirms at apply; stop-and-ask if judged otherwise).
- **Docs:** `docs/deploying-the-gateway.md` → `docs/deploying-the-network.md` (+ content sweep); `daemon/Daemon.App/README.md` note on installing via `make network` / `dotnet tool install ndmon`, the `~/.dotnet/tools/ndmon` default, and the `DMON_NETWORK_PATH` override.

No change to the host's **runtime behaviour**, the wire protocol, or the remote-access semantics (ADR-012). This is packaging + naming.

## Capabilities

### New Capabilities

_(none — the host already exists; this change makes it installable and renames it.)_

### Modified Capabilities

- `package-publishing`:
  - **ADD** "`Dmon.Network` published as a dotnet tool" (`PackAsTool`, command `ndmon`, resolves to `~/.dotnet/tools/ndmon`).
  - **MODIFY** "Only the five distribution projects are packable" — app-artifact tools (the renamed `Dmon.Network`) are packable, distinct from the protocol-keyed NuGet set.
  - **MODIFY** "Protocol-keyed three-part version scheme" — carve out app-artifact dotnet tools from the protocol-lockstep `Major.Minor` gate (independent versioning per ADR-024).
- `remote-session-gateway`:
  - **MODIFY** "Gateway session-create control frame" → "Network session-create control frame" (terminology only; behaviour unchanged). Broader prose terminology sweep tracked as an in-change task on the standing spec.

## Impact

- **Code / build:** renamed `frontends/Dmon.Network` (csproj + ~30 sources + all `Network*` types), `test/Dmon.Network.Tests`, `frontends.slnx`/`Everything.slnx`, ProjectReferences; `PackAsTool`/`ToolCommandName=ndmon`/`PackageId`/`IsPackable`; `Makefile` `network` target; the version-consistency / packability CI check must treat the tool as an independently-versioned app artifact (or pack fails CI).
- **dmonium:** `NetworkManager`, "Network" health row + label, `DMON_NETWORK_PATH`, default `~/.dotnet/tools/ndmon`. The row turns green OOTB once `ndmon` is installed.
- **Specs / ADRs / docs:** `package-publishing` + `remote-session-gateway` deltas; new ADR-033 + terminology amendments to ADR-012/017/018/028; doc rename + sweep.
- **Cross-change coordination risk:** the other active change `daemon-scheduler` references `Dmon.Gateway` textually in its (unapplied) artifacts; those will go stale and must be reconciled when `daemon-scheduler` is applied. Flagged, not edited here.
- **No change** to dmon-core, the wire protocol, the host's runtime behaviour, or the first-party protocol-keyed NuGet release train.
- **Note:** this change bundles a packaging fix and a full rebrand per explicit direction; it is larger than a typical single change. It could be split (packaging first, rebrand second) if review burden warrants — recorded, not assumed.
