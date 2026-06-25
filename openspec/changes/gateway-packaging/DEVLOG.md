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

## ✅ USER DECISION (2026-06-25): Option B — FULL clean break on runtime config strings.
The user chose to ALSO rename the .NET runtime config seam (not keep it). So the deliberately-deferred strings from Blocks 1–4 now get renamed in a NEW code block before Group 9:
- config section `Gateway`→`Network`: `NetworkOptions.SectionName="Gateway"`→`"Network"`, every `GetSection("Gateway")`→`GetSection("Network")`, the `appsettings.json` `"Gateway"` block → `"Network"`.
- log prefix `[dmon-gateway]`→`[dmon-network]`.
- on-disk device-key store `~/.dmon/gateway`→`~/.dmon/network` (clean break, no migration per no-prod-deployments).
- ADR-033 must be AMENDED: its Decision 4(b) currently records these as "deferred/retained" — flip it to "renamed as part of this change" (the wire/contract non-rename 4(a) STILL stands).
- proposal.md / design.md Non-Goals that said "no runtime behaviour change" / "no rename of runtime config" must be realigned to reflect the expanded scope.
- After this block: Group 9 docs sweep FULLY to `Network:*`; Group 10 grep gate is clean with NO runtime-config exemption (only the permanent-identifier exemptions remain: ADR filenames, `remote-session-gateway` capability id, `Dmon.Protocol.Gateway` namespace + `gw` discriminator, archived changes).
- ⚠ HAZARD: this changes the config-section name the host reads + the on-disk key-store path. Any test fixtures / appsettings / sample configs that set `"Gateway":` or point at `.dmon/gateway` must move in lockstep or auth/bind tests break. The `remote-session-gateway` capability id and the `Dmon.Protocol.Gateway` wire namespace are NOT config strings — leave them.

## Block 6 — tasks 11.1–11.5 (Option B runtime config rename — DONE, committed)

Renamed the .NET host's runtime config-string surface (clean break). Worker swept BROADER than the architect's minimal enumeration (reviewer-approved as in-scope for Option B's full clean break):
- `NetworkOptions.SectionName "Gateway"→"Network"` + appsettings.json `"Gateway"`→`"Network"` block (lockstep CONFIRMED — const and appsettings agree; `Program.cs` reads `GetSection(NetworkOptions.SectionName)`).
- `[dmon-gateway]`→`[dmon-network]` log prefix; `.dmon/gateway`→`.dmon/network` store path; `gatewayOptions`→`networkOptions` local.
- **`NetworkBindPolicy.cs` 5 user-visible error messages** incl. config-key refs `Gateway:BindAddress`→`Network:BindAddress`, `Gateway:AllowNonLoopbackBind`→`Network:AllowNonLoopbackBind` (CORRECTNESS: these keys live under the now-`Network` section — telling users `Gateway:*` would be wrong).
- **`NetworkConnectionEndpoint.cs` runtime message** "The gateway has reached…"→"The network host has reached…" + matching test assertion in `NetworkCreateFlowTests.cs` (legitimate full-string-equality assertion tracking the changed message, NOT a weakened test).
- DeviceKeys/* + test comment prose swept.
- **Wire contract FROZEN (ADR-033 4a):** `Dmon.Protocol.Gateway` namespace/imports, `gw` discriminator, control-frame codes (`unknown_agent`/`core_timeout`/`cap_reached`) byte-identical. Only the `gw` heartbeat doc-prose narration changed (`gateway→client`→`host→client`), discriminator literal preserved.
- Orchestrator artifact realignments (same working tree, committed together): ADR-033 Decision 4(b)+Consequences+Alternatives+OpenQuestions flipped from "deferred" to "renamed in this change"; proposal.md L27/L50 + design.md Goals/Non-Goals softened ("runtime config + on-disk path ARE renamed; wire/semantics unchanged"). tasks.md gained Group 11.
- Gates: build 0-warn, test all green (Network 208/208), validate --strict. `git grep gateway` over tracked Dmon.Network src+tests = ONLY `Dmon.Protocol.Gateway` wire imports.

## Remaining: Group 9 (docs) → Group 10 (grep gate + final validation + human-verify 10.4).
- **Config-string entanglement now RESOLVED** — Group 9 docs sweep FULLY to `Network:*` / `~/.dmon/network` (the runtime now matches). Group 10's 10.1 grep gate is clean with NO runtime-config exemption.
- **10.1 permanent-identifier exemption set** (reviewer-confirmed, these legitimately retain "gateway"): the `remote-session-gateway` capability id (spec folder/heading/Purpose); the `Dmon.Protocol.Gateway` wire namespace + `gw` discriminator; ADR filenames `ADR-012-*`/`ADR-017-*`/`ADR-018-per-device-gateway-keys.md`; `openspec/changes/archive/**`; the active `daemon-scheduler` change's stale refs (flag, don't edit). Also git-ignored `obj/`/`bin/` build artifacts (stale `Dmon.Gateway.*`, regenerated on clean rebuild).
- **10.4 is a HUMAN-VERIFY** (launch dmonium, confirm Network row green OOTB) — orchestrator hands the recipe to the user; cannot self-gate.
- Group 9 + 10 can be ONE final block.

## Block 7 (FINAL worker block) — tasks 9.1, 9.2, 10.1, 10.2, 10.3 (DONE, committed)

Docs sweep + completeness grep gate + full gate sweep. 2 files.
- **9.1:** `git mv docs/deploying-the-gateway.md → docs/deploying-the-network.md` + full sweep: title→"Deploying the dmon Network host"; `Dmon.Gateway`→`Dmon.Network`; config table `Gateway:*`→`Network:*` (all 7 keys) + appsettings `"Network"` block + `GATEWAY__SharedKey`→`NETWORK__SharedKey`; stale run path `src/Dmon.Gateway`→`frontends/Dmon.Network` + `ndmon`/`make network` install block; `~/.dmon/gateway`→`~/.dmon/network`; superseded ADR-013 link→ADR-022; `tag:gateway-server`→`tag:network-host`. No shipping inbound links needed fixing.
- **9.2:** `daemon/Daemon.App/README.md` — `DMON_GATEWAY_PATH`→`DMON_NETWORK_PATH` + install note; "Gateway" component prose→"Network host"; `GatewayManager.swift`→`NetworkManager.swift` file-list entry. Test list already correct.
- **10.1 grep gate (rename-completeness, the achievable form):** Pass-A (`Dmon.Gateway|DMON_GATEWAY_PATH|deploying-the-gateway`, exempt paths excluded) = ZERO; Pass-B (`gateway` in the two edited docs) = ZERO. Reviewer independently re-ran both → confirmed ZERO.
- **10.3 full gates (orchestrator-run):** validate --strict ✓; make build 0-warn; make test 0 failures (Core 591/1, all suites); swift build complete; swift test "All tests passed" (72/72).
- **Config-value correctness verified:** reviewer cross-checked the doc's `Network:*` keys against shipped `NetworkOptions.cs`/`NetworkBindPolicy.cs` — match.

### Flags / follow-ups (out of scope for this change — NOT blockers):
- **daemon-scheduler stale refs** (`openspec/changes/daemon-scheduler/{design,proposal,tasks}.md` reference `Dmon.Gateway`/`frontends/Dmon.Gateway`, 3 locations) — in the exempt active-change path, FLAGGED not edited. Reconcile when `daemon-scheduler` is applied (cross-change risk already noted in proposal Impact).
- **deploy doc `SharedKey` doc-drift** (reviewer nit): `docs/deploying-the-network.md` documents `Network:SharedKey`/`NETWORK__SharedKey` (Step 4 + reference table), but the shipped `NetworkOptions` has NO `SharedKey` — auth is per-device keys (`DeviceKeyAuthenticator` + `devices.json`, ADR-018, which superseded shared-key in a prior merged change). This is PRE-EXISTING drift (`git show HEAD:docs/deploying-the-gateway.md` carried identical `Gateway:SharedKey` content) faithfully renamed by the sweep — re-validating the doc against the ADR-018 device-key model is OUTSIDE gateway-packaging's "rename, not content audit" charter. Recommend a separate follow-up change to rewrite Step 4 + the reference table around per-device keys.
- **`docs/protocol/README.md`** (titled "dmon WebSocket Gateway", ~30 host-role uses) deliberately left as host-role vocabulary per ADR-033; a possible future rebrand, not in this change's spec deltas.

## ⏳ Block 8 — 10.4 HUMAN-VERIFY (orchestrator → user): launch dmonium, confirm Network health row green OOTB with no DMON_NETWORK_PATH. Recipe handed to user. NOT yet ticked.

---

**[Earlier stop-and-ask framing — SUPERSEDED by the Option-B decision; kept for history:]**
- The architect (Block 5 planning) verified `docs/deploying-the-gateway.md` documents the runtime config keys extensively: `Gateway:BindAddress`, `Gateway:SharedKey`, `Gateway:AllowNonLoopbackBind`, `GATEWAY__SharedKey`, the full `Gateway:*` config table, plus `~/.dmon/gateway`. Group 9.1's "sweep its content" CANNOT honestly rename those to `Network:*` UNLESS the runtime `NetworkOptions.SectionName="Gateway"` / `GetSection("Gateway")` / `appsettings.json "Gateway"` block / `.dmon/gateway` store are ALSO renamed — which is the runtime-config + on-disk behavioural change the proposal scoped OUT ("no change to the host's runtime behaviour").
- So Group 9 is BLOCKED on a user decision: **(A)** keep the runtime config seam as-is (`SectionName="Gateway"`, `.dmon/gateway`) and have the docs continue to document the real `Gateway:` keys (doc sweep limited to host-name/`ndmon`/`make network`/`DMON_NETWORK_PATH`; the `Gateway:*` config table stays literally `Gateway:` because that's what the runtime reads); explicitly EXEMPT those strings from the 10.1 grep gate alongside the capability id. OR **(B)** expand scope: rename the runtime config section + on-disk store to `Network`/`.dmon/network` (a real behavioural change, needs its own go-ahead and possibly an ADR-033 amendment + appsettings + a SECTION rename in Dmon.Network code), then docs sweep fully and 10.1 is clean with no config exemption.
- **10.1 grep-gate exemption set (reviewer-confirmed permanent identifiers regardless of A/B):** ADR filenames `ADR-012-*`, `ADR-017-*`, `ADR-018-per-device-gateway-keys.md`; capability id/folder/heading/Purpose `remote-session-gateway` (spec L1/L4); the `Dmon.Protocol.Gateway` namespace + `gw` discriminator; archived `openspec/changes/archive/**`. Under option A, ADD the runtime config strings to this set.
- Group 9 (docs) and Group 10 (grep gate) should be ONE final block, planned only AFTER the A/B decision.
- **CRITICAL for whoever plans Group 10 — the runtime config-string decision:** the .NET-side strings `NetworkOptions.SectionName="Gateway"`, `GetSection("Gateway")`, `[dmon-gateway]` log prefix, and `.dmon/gateway` device-key store were INTENTIONALLY left untouched by Blocks 1–4 (they are the runtime config contract / on-disk seam, NOT C# identifiers or dmonium). Task 10.1's grep gate says "no stray `Dmon.Gateway`/`Gateway`/`gateway`/`DMON_GATEWAY_PATH` … outside the retained `remote-session-gateway` id and archived changes." Those .NET runtime strings WILL hit that grep. **Decide at Group 10 planning:** either (a) 10.1 must also rename them (additional scope beyond Blocks 1–4's deliberate line — and renaming `SectionName`/`.dmon/gateway` is a runtime-config/on-disk-path change with its own behavioural surface, possibly a stop-and-ask), or (b) they're explicitly exempted in the grep gate like the capability id. The task wording does NOT currently exempt them → architect must resolve this explicitly, not silently. Do NOT let a worker quietly rename `SectionName`/the key-store path to satisfy a grep without surfacing the behavioural implication.
- Group 8 applies the `remote-session-gateway` spec DELTA already authored in `openspec/changes/gateway-packaging/specs/remote-session-gateway/spec.md` (terminology only; capability id retained).
