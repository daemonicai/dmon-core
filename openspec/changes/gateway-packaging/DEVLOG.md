# DEVLOG — gateway-packaging

Packaging fix + full `Gateway`→`Network` rebrand. 28 tasks, 10 groups. Branch `change/gateway-packaging`.

## Pinned facts (read before planning any block)

- **Wire/contract strings are NOT renamed.** Control-frame strings (`create`/`created`/`attach`/`sessionId`/`agent`), the `gw` discriminator, and the shared protocol namespace `Dmon.Protocol.Gateway` (a separate contract package, consumed via `using Dmon.Protocol.Gateway;`) stay as-is. The rebrand is host-side only (ADR-012/ADR-015 hold).
- **Runtime config/on-disk strings still say "Gateway" after Block 1 — deliberately.** `NetworkOptions.SectionName = "Gateway"`, `GetSection("Gateway")`, `[dmon-gateway]` console log prefixes in `Program.cs`, and the `.dmon/gateway` device-key store directory were left untouched by Block 1 (pure type/namespace rename). **Decide explicitly in a later block** whether these become "Network"/`.dmon/network` — they are the config contract / on-disk seam, not C# identifiers. Not yet assigned to a task; flag if a block needs them.
- **Capability id `remote-session-gateway` is retained** (internal identifier, high cross-ref churn) — terminology sweep inside the spec only (task 8.1), id/folder unchanged.
- **Group 4 packaging hazard (architect's forward note):** the version-skew guard lives in `Directory.Build.props` `CheckProtocolVersionSkew` target (~lines 64–96), which fires for any `IsPackable=true` project and would reject `Dmon.Network`'s independent version. Task 4.4's carve-out must edit that target, not just spec text. `ndmon` is an app artifact, independently versioned, EXEMPT from the protocol-keyed `Major.Minor` gate (ADR-024).
- **Out of scope / do not edit:** `openspec/changes/archive/**`; the active `daemon-scheduler` change's stale `Dmon.Gateway` references (flag at its apply, don't edit here).
- **Gates:** `make build` (0-warn, TreatWarningsAsErrors), `env -u MEKO_API_KEY make test` (avoids live-Meko smoke hang), `openspec validate gateway-packaging --strict`. dmonium blocks (Group 6) add `swift build -c release --package-path daemon/Daemon.App` + `swift test`.

## Block 1 — tasks 1.1–1.3, 2.1–2.2, 3.1–3.2 (DONE, committed)

Mechanical rename of the host project. `git mv frontends/Dmon.Gateway → frontends/Dmon.Network` (csproj, `RootNamespace`/`AssemblyName`→`Dmon.Network`, `InternalsVisibleTo`→`Dmon.Network.Tests`); 7 `Gateway*`-named source files renamed (`NetworkBindPolicy`, `NetworkConnectionEndpoint`, `NetworkDeviceKeyPaths`, `NetworkOptions`, `NetworkProfilePaths`, `INetworkConnection`, `WebSocketNetworkConnection`); all `namespace Dmon.Gateway*`→`Dmon.Network*` across 20 production sources; `test/Dmon.Gateway.Tests → test/Dmon.Network.Tests` (csproj repathed; 3 `Gateway*` test classes renamed to `Network*`); `frontends.slnx` + `Everything.slnx` repathed.

- **Kept `OutputType=Exe`, `IsPackable=false`** — no packaging yet (Group 4 owns it).
- Wire shape byte-identical; `ByteUnchangedForwardingTests`/`ControlFrameTests` green.
- Reviewer: clean pure rename, approved, no blockers.
- Gates: build 0-warn, test all green (Dmon.Network.Tests 208/208), validate --strict ✓.
- Cosmetic only: stale `Dmon.Gateway.*` files linger in git-ignored `obj/` until next `make clean`.

## Block 2 — tasks 4.1–4.5 (DONE, committed)

`Dmon.Network` is now a packable `PackAsTool` global tool, command `ndmon`, independently versioned and exempt from the protocol-version gate. Two files only:

- **`Dmon.Network.csproj`:** `IsPackable=true`, `PackAsTool=true`, `PackageId=Dmon.Network`, `ToolCommandName=ndmon`, `IsProtocolKeyedPackage=false`, `MinVerVersionOverride=0.1.0`, `ErrorOnDuplicatePublishOutputFiles=false`.
- **`Directory.Build.props`:** added `<IsProtocolKeyedPackage>true</IsProtocolKeyedPackage>` default (shared PropertyGroup) + tightened the `CheckProtocolVersionSkew` target condition to `'$(IsPackable)' == 'true' and '$(IsProtocolKeyedPackage)' != 'false'` (fail-closed `!= 'false'` form — a package missing the property stays gated).

**Resolved decisions (for next architect):**
- **Carve-out mechanism = `IsProtocolKeyedPackage` opt-out property** (defaults true; only `Dmon.Network` sets false). This is the reusable seam for any future app-artifact tool. Guard verified still-fires for protocol-keyed packages (`core/Dmon.Protocol` @0.9.0 → errors), does NOT fire for the tool (@0.1.0).
- **4.2 license already satisfied centrally:** `Directory.Build.props` line ~15 has `<PackageLicenseExpression>MPL-2.0</PackageLicenseExpression>` + authors/repo/projectUrl; nuspec inherits them, no NU5125/NU5128. Worker correctly added nothing.
- **Web SDK + PackAsTool:** `Microsoft.NET.Sdk.Web` causes NETSDK1152 (transitive `appsettings.json` dup). Resolved with `ErrorOnDuplicatePublishOutputFiles=false` (documented standard fix, behaviour-neutral) — SDK NOT changed (host needs ASP.NET Core stack). Tool layout valid: single `tools/net10.0/any/` entrypoint `Dmon.Network.dll`, command `ndmon`.
- **Independent version = `0.1.0`** (deliberately ≠ protocol 0.2.x so the exemption is exercised); `MinVerVersionOverride` scoped to this csproj only, no leak.

**Open carry-forward (NOT a blocker, for a later block / orchestrator):** the real release pipeline (`scripts/pack-core.sh` / `.github/workflows/release.yml`) is an explicit allow-list and was deliberately NOT touched. If `ndmon` should ship via that pipeline it needs a later wiring decision — proposal/design treat nuget.org publish of the tool as a follow-on (out of scope here). 4.5 was satisfied by local-pack demonstration, not a pipeline edit.

## Block 3 — tasks 5.1–5.2 (DONE, committed)

`make network` target added to the `Makefile` (only file changed). Shape mirrors `daemon-app`:
```make
network:
	dotnet pack frontends/Dmon.Network/Dmon.Network.csproj -c $(CONFIG) -o "$(PACK_OUT)"
	dotnet tool update --global --add-source "$(abspath $(PACK_OUT))" Dmon.Network --version 0.1.0 \
		|| dotnet tool install --global --add-source "$(abspath $(PACK_OUT))" Dmon.Network --version 0.1.0
```
- **Decided (D3 open question closed): GLOBAL install** (not `--tool-path`), idempotent via `update || install`. On .NET 10 `dotnet tool update --global` install-or-updates a missing tool; the `|| install` is a belt-and-suspenders for older SDKs. Both runs exit 0; `~/.dotnet/tools/ndmon` resolves; `dotnet tool list --global` → `dmon.network 0.1.0 ndmon`.
- Packs `Dmon.Network` DIRECTLY with `dotnet pack` (NOT via `pack-core.sh` — the protocol-keyed allow-list stays out of scope). Reuses existing `$(CONFIG)`/`$(PACK_OUT)` vars; `$(abspath …)` required for the `--add-source` feed.
- `ndmon` is now installed globally on this dev machine — leave it (5.2/10.4 depend on it). This closes the original [[gateway-packaging-gap]]: dmonium's default Network path is produced OOTB.
- Gates: build 0-warn, test 929 passed/2 skipped, validate --strict ✓.

**Reviewer nits (non-blocking, carry-forward for the eventual release-pipeline change):** (1) `--version 0.1.0` is hard-coded in the Makefile → must hand-sync with the csproj `MinVerVersionOverride` on any bump; a version-free `dotnet tool update` from the feed would drop the coupling. (2) `.pack-out` accumulates nupkgs; the explicit `--version` pin neutralizes any ambiguity today. Both are first-cut acceptable (D1: "local install from pack output is the first cut").

## Block 4 — tasks 6.1–6.5 (DONE, committed)

Full dmonium (`daemon/Daemon.App`) Swift `Gateway`→`Network` rebrand. 13 files, 114/114 (pure rename). `GatewayManager.swift`→`NetworkManager.swift` (git mv).
- **`gatewayStopped` symbol set → `networkStopped`** renamed in lockstep across `HealthRegistry.swift` (private var, `setNetworkStopped`, `observeNetworkStopped`, `rollup(networkStopped:)` arg label), the `DaemonController` call site, and all ~18 `rollup(...)` call sites in `HealthClassificationTests`. Rollup body intact: `if networkStopped { return .red }` first check preserved (stopped→red contract).
- **`@EnvironmentObject` is type-resolved** — every `: NetworkManager` declaration matched by `.environmentObject(controller.network)` (`DaemonApp`, `DashboardView`×2, `MenuBarView`, `SettingsView`). Consistent, no silent runtime crash.
- 6.3: default path → `~/.dotnet/tools/ndmon` (`NetworkManager.swift` `.appendingPathComponent(".dotnet/tools/ndmon")`). 6.4: `DMON_GATEWAY_PATH`→`DMON_NETWORK_PATH` clean break (no alias) in `NetworkManager` + `SettingsView` (env read, load/save map, "Network binary path" field + help); `gatewayPath`→`networkPath`/`networkPathOverride` plumbing consistent.
- **Worker judgement call (reviewer-approved):** renamed the dmonium-owned PID file `.dmon/run/gateway.pid` → `network.pid` (D4 didn't map runtime on-disk paths; chosen for the 10.1 grep gate). Sound: written + re-adopted by the same `ServerProcessManager`, no external/.NET reader, no migration concern. **The old `gateway.pid` was deliberately abandoned, not missed.**
- **Confirmed `DMON_GATEWAY_PATH` is dmonium-only** — read NOWHERE on the .NET side, so the clean break is fully contained.
- Gates ALL independently re-run by orchestrator: swift build 0-err, swift test 72/0, make build 0-warn, make test green, validate --strict, `grep -rni gateway daemon/Daemon.App/{Sources,Tests}` = zero. (SourceKit IDE diagnostics flagged spurious "Cannot find type" false positives — no SwiftPM build graph; real swift build clean.)

## Block 5 — tasks 7.1, 7.2, 8.1 (DONE, committed)

ADR-033 + terminology amendments + standing-spec prose. 6 files (5 modified + new ADR-033).
- **ADR-033** (`docs/adrs/ADR-033-rename-gateway-host-to-network.md`, status Accepted, Amends 012/017/018/028 terminology-only, Builds-on 024). Records the rename AND the two deliberate non-renames: (a) wire/contract strings (`create`/`created`/`attach`, `gw`, `Dmon.Protocol.Gateway`, `seq`/`headSeq`/`generation`); (b) runtime config-section/on-disk strings (`SectionName="Gateway"`, `GetSection("Gateway")`, `[dmon-gateway]`, `~/.dmon/gateway`) deferred/retained (ADR-033 line 32 captures this verbatim).
- **7.2 amendments:** one-line terminology note under `**Status:**` in ADR-012 (L6), ADR-017 (L7), ADR-018 (L7), ADR-028 (L10, additive — did NOT clobber the prior dmonium-windowed-dashboard amendment). Reviewer independently read every numbered decision of all four → "Gateway" is always descriptive host-naming, NO numbered decision reversed. Recording-ADR shape confirmed correct (not a supersession).
- **8.1:** standing spec `openspec/specs/remote-session-gateway/spec.md` — requirement `Gateway session-create control frame`→`Network session-create control frame` (matches authored delta byte-for-byte); ~35 "the gateway"→"the network host" prose sweeps. Capability id/folder/heading `remote-session-gateway` RETAINED; no wire literal renamed.
- **Reviewer nit fixed by orchestrator** (doc-only realignment): line 29 `client→gateway:`/`gateway→client:` were host-role prose (not wire literals) → swept to `client→host`/`host→client`. Now the ONLY `gateway` residues in the standing spec are the capability-id heading (L1) + Purpose boilerplate (L4), both deliberately retained.
- Gates: validate --strict ✓; build/test regression-only (no compiled code), green.

## Remaining: Group 9 (docs) + Group 10 (grep gate + final validation + human-verify 10.4).
**⛔ STOP-AND-ASK PENDING (must resolve with the USER before Group 9 worker starts) — the runtime config-string decision:**
- The architect (Block 5 planning) verified `docs/deploying-the-gateway.md` documents the runtime config keys extensively: `Gateway:BindAddress`, `Gateway:SharedKey`, `Gateway:AllowNonLoopbackBind`, `GATEWAY__SharedKey`, the full `Gateway:*` config table, plus `~/.dmon/gateway`. Group 9.1's "sweep its content" CANNOT honestly rename those to `Network:*` UNLESS the runtime `NetworkOptions.SectionName="Gateway"` / `GetSection("Gateway")` / `appsettings.json "Gateway"` block / `.dmon/gateway` store are ALSO renamed — which is the runtime-config + on-disk behavioural change the proposal scoped OUT ("no change to the host's runtime behaviour").
- So Group 9 is BLOCKED on a user decision: **(A)** keep the runtime config seam as-is (`SectionName="Gateway"`, `.dmon/gateway`) and have the docs continue to document the real `Gateway:` keys (doc sweep limited to host-name/`ndmon`/`make network`/`DMON_NETWORK_PATH`; the `Gateway:*` config table stays literally `Gateway:` because that's what the runtime reads); explicitly EXEMPT those strings from the 10.1 grep gate alongside the capability id. OR **(B)** expand scope: rename the runtime config section + on-disk store to `Network`/`.dmon/network` (a real behavioural change, needs its own go-ahead and possibly an ADR-033 amendment + appsettings + a SECTION rename in Dmon.Network code), then docs sweep fully and 10.1 is clean with no config exemption.
- **10.1 grep-gate exemption set (reviewer-confirmed permanent identifiers regardless of A/B):** ADR filenames `ADR-012-*`, `ADR-017-*`, `ADR-018-per-device-gateway-keys.md`; capability id/folder/heading/Purpose `remote-session-gateway` (spec L1/L4); the `Dmon.Protocol.Gateway` namespace + `gw` discriminator; archived `openspec/changes/archive/**`. Under option A, ADD the runtime config strings to this set.
- Group 9 (docs) and Group 10 (grep gate) should be ONE final block, planned only AFTER the A/B decision.
- **CRITICAL for whoever plans Group 10 — the runtime config-string decision:** the .NET-side strings `NetworkOptions.SectionName="Gateway"`, `GetSection("Gateway")`, `[dmon-gateway]` log prefix, and `.dmon/gateway` device-key store were INTENTIONALLY left untouched by Blocks 1–4 (they are the runtime config contract / on-disk seam, NOT C# identifiers or dmonium). Task 10.1's grep gate says "no stray `Dmon.Gateway`/`Gateway`/`gateway`/`DMON_GATEWAY_PATH` … outside the retained `remote-session-gateway` id and archived changes." Those .NET runtime strings WILL hit that grep. **Decide at Group 10 planning:** either (a) 10.1 must also rename them (additional scope beyond Blocks 1–4's deliberate line — and renaming `SectionName`/`.dmon/gateway` is a runtime-config/on-disk-path change with its own behavioural surface, possibly a stop-and-ask), or (b) they're explicitly exempted in the grep gate like the capability id. The task wording does NOT currently exempt them → architect must resolve this explicitly, not silently. Do NOT let a worker quietly rename `SectionName`/the key-store path to satisfy a grep without surfacing the behavioural implication.
- Group 8 applies the `remote-session-gateway` spec DELTA already authored in `openspec/changes/gateway-packaging/specs/remote-session-gateway/spec.md` (terminology only; capability id retained).
