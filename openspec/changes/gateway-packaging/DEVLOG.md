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

## Remaining: Group 6 (dmonium/Swift rebrand — needs swift build/test gates), Group 7 (ADRs), Group 8 (standing-spec prose), Group 9 (docs), Group 10 (grep gate + final validation + human-verify 10.4).
- **Group 6 is the next natural block** — it's the only remaining block with the Swift toolchain gate (`swift build -c release --package-path daemon/Daemon.App` + `swift test`). It also finally renames the dmonium default path to `~/.dotnet/tools/ndmon` and `DMON_GATEWAY_PATH`→`DMON_NETWORK_PATH`, making the OOTB green-row deliverable real end-to-end.
- **Reminder for whoever plans Group 10:** the grep gate (10.1) deliberately retains the `remote-session-gateway` capability id and archived changes; and the runtime config strings (`NetworkOptions.SectionName="Gateway"`, `GetSection("Gateway")`, `[dmon-gateway]` log prefix, `.dmon/gateway` key store) were INTENTIONALLY left by Blocks 1–3. Decide at Group 10 planning whether 10.1's "no stray `gateway`" gate requires renaming those runtime/config strings too, or whether they're explicitly exempt like the capability id. If they must change, that's additional scope beyond the current task wording — flag/stop-and-ask rather than silently expanding.
