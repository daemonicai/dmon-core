## Context

`frontends/Dmon.Gateway` is the WebSocket remote-access host (ADR-012, `remote-session-gateway` spec): connection-decoupled resumable sessions, Tailscale-fronted auth, per-device keys (ADR-018), agent-bound `create`/`attach` control frames. It builds today only as a plain console exe; there is no pack or install step, so dmonium's default candidate `~/.dotnet/tools/Dmon.Gateway` never exists and the health row is red until a user hand-builds a binary and sets `DMON_GATEWAY_PATH`.

This change does two things at once on the same files: (1) the packaging fix, and (2) a product rename — the host becomes **`Dmon.Network`**, the tool command **`ndmon`**, and the "gateway" terminology is dropped everywhere (code, dmonium, the standing capability prose, docs, ADR wording).

Binding ADRs: **ADR-024/ADR-028** make the host an app artifact, independently versioned, off the protocol-lockstep NuGet train; **ADR-011/ADR-025** frame distribution and the monorepo release families; **ADR-012/017/018** define the remote-access behaviour and carry "Gateway" terminology.

Reference surface measured: ~30 sources under `frontends/Dmon.Gateway/` (all `namespace Dmon.Gateway`, many `Gateway*` types), `test/Dmon.Gateway.Tests` (~20 files), `frontends.slnx` + `Everything.slnx`, dmonium `GatewayManager.swift`/`SettingsView.swift`/`DaemonController.swift`, `openspec/specs/remote-session-gateway/spec.md`, `docs/deploying-the-gateway.md`, and ADR-012/017/018/028.

## Goals / Non-Goals

**Goals:**
- The host is installable in one command and resolves at `~/.dotnet/tools/ndmon`, so dmonium's Network row is green OOTB.
- The host is fully renamed to `Dmon.Network` / `ndmon` with no "gateway" terminology remaining in first-class code, the dmonium UI, docs, or ADR prose.
- Keep the host an independently-versioned app artifact (ADR-024), exempt from the protocol-keyed version gate.
- Leave the host's runtime behaviour and the wire protocol unchanged.

**Non-Goals:**
- No behavioural change to remote access (ADR-012 semantics intact) — terminology only.
- No nuget.org publish of the `ndmon` tool in this change (local `dotnet tool install` from pack output is the first cut).
- No rename of the `remote-session-gateway` capability **id/folder** (internal openspec identifier; high cross-reference churn) — terminology inside the spec only.
- No code-signing/notarisation; no bundling into the dmonium `.app`.
- No edits to the unrelated active `daemon-scheduler` change (its stale `Dmon.Gateway` references are its own apply-time concern).

## Decisions

### D1: `Dmon.Network` becomes a `PackAsTool` tool, command `ndmon`
Add `PackAsTool=true`, `IsPackable=true`, `PackageId=Dmon.Network`, and `ToolCommandName=ndmon` to the renamed `Dmon.Network.csproj`. `dotnet pack` yields a tool package; a global `dotnet tool install` places `~/.dotnet/tools/ndmon`. dmonium's default candidate becomes exactly that path.
- **Why `ndmon` is unambiguous (no casing trap):** unlike the prior `Dmon.Gateway` candidate (whose required filename casing was a risk), `ToolCommandName=ndmon` deterministically produces `~/.dotnet/tools/ndmon`. dmonium's Swift default is updated to match.

### D2: Independent versioning — exempt from the protocol-keyed gate
The `Dmon.Network` tool versions on its own cadence (ADR-024), not the protocol-keyed `Major.Minor`. The `package-publishing` "Protocol-keyed three-part version scheme" requirement (which would reject any published package whose `Major.Minor ≠ ProtocolVersion.Current`) is amended to exempt app-artifact dotnet tools; the "packable projects" requirement is amended to admit them as packable-but-not-protocol-keyed. **The build/release version-consistency check that enforces the gate must implement this carve-out or the `Dmon.Network` pack fails CI** (task 2).

### D3: `make network` target
Add a `network` target to the `Makefile` (build → pack → install to the default dotnet-tools location), mirroring `make daemon-app`; idempotent (update if already installed). Open: global `dotnet tool install` vs `--tool-path ~/.dotnet/tools` (both land the same path; global is conventional).

### D4: Mechanical rename mapping (full rebrand)
- `frontends/Dmon.Gateway/` → `frontends/Dmon.Network/`; `Dmon.Gateway.csproj` → `Dmon.Network.csproj`; `RootNamespace`/`AssemblyName` → `Dmon.Network`.
- `namespace Dmon.Gateway` → `namespace Dmon.Network` (all sources); `Gateway*` types → `Network*`.
- `test/Dmon.Gateway.Tests` → `test/Dmon.Network.Tests` (dir, csproj, namespaces, `@testable`/using references).
- `frontends.slnx`, `Everything.slnx`, ProjectReferences repathed.
- **dmonium:** `GatewayManager.swift`/class → `NetworkManager`; health component `"Gateway"` → `"Network"`; `observeGatewayStopped` → `observeNetworkStopped`; the "special icon role (stopped → red)" wording follows; `DaemonController.gateway` → `.network`; Settings "Gateway binary path" → "Network binary path"; default path → `~/.dotnet/tools/ndmon`; config/env key `DMON_GATEWAY_PATH` → `DMON_NETWORK_PATH`.
- **Clean break on the config key** (`DMON_GATEWAY_PATH` → `DMON_NETWORK_PATH`): no back-compat alias, per the project's "no production deployments / prefer clean breaks" stance.

### D5: ADR-033 records the rename; ADR-012/017/018/028 get terminology amendments
Write **ADR-033** ("Rename the gateway host to `Dmon.Network` / `ndmon`") capturing the decision and rationale, and add one-line terminology amendment notes to ADR-012/017/018/028 (the "Gateway" wording → "Network host"). No numbered ADR *decision* is reversed (remote-access architecture, Tailscale boundary, per-device keys, bucket placement all stand) — this is a naming decision, so a recording ADR + amendments, **not** a supersession. If the reviewer judges any "Gateway" usage was load-bearing in a numbered decision, that is a stop-and-ask.

### D6: Standing-spec terminology, capability id retained
Update "the gateway" → "the network host" terminology throughout `remote-session-gateway/spec.md`, and rename the one title-bearing requirement ("Gateway session-create control frame" → "Network session-create control frame"). **Keep the capability id/folder `remote-session-gateway`** — it is an internal identifier referenced by archived changes; renaming it is high-churn and orthogonal to the product rename. (Open question: rename the capability id in a later dedicated change.)

## Risks / Trade-offs

- **[Large, bundled diff]** packaging + a ~50-file rebrand in one change is heavy to review. Mitigation: group tasks so the mechanical rename, the packaging, dmonium, ADR/spec, and docs are separable commits; the change can still be split if review burden warrants (proposal note).
- **[Version-gate CI rejects the tool]** → D2: the carve-out must reach the actual enforcement check, not just the spec text.
- **[Renaming a concept in accepted ADRs looks like supersession]** → D5: terminology-only amendments + a recording ADR; stop-and-ask if a reviewer disagrees.
- **[Cross-change staleness]** → `daemon-scheduler` (active, unapplied) references `Dmon.Gateway`; it will need reconciliation when applied. Flagged in Impact; not edited here.
- **[Config-key clean break]** → `DMON_NETWORK_PATH` silently ignores any old `DMON_GATEWAY_PATH` a user had set; acceptable per no-prod-deployments, called out in docs.
- **["gateway" residue]** → a full sweep risks missing occurrences (comments, doc anchors, the `remote-session-gateway` id we deliberately keep). A grep gate (no stray `Gateway`/`gateway` outside the retained capability id and archived changes) guards completeness.

## Open Questions

- `make network`: global `dotnet tool install` vs explicit `--tool-path`?
- Rename the `remote-session-gateway` capability id/folder (and its cross-references) in a follow-up, or leave it permanently?
- Wire `ndmon` into the tag-driven nuget.org release pipeline now, or as a follow-on (leaning follow-on)?
- ADR-033 as a standalone recording ADR vs folding the rename note into each amended ADR only — confirm with the reviewer at apply.
