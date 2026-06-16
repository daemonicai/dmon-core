# DEVLOG: monorepo-phase0-reorg

<!-- Phase 0 of ADR-025: reorganise the existing dmon-core projects into the monorepo bucket layout (structural only; CI stays green). -->

## 1. Cleanup ghost dirs and cruft

- Removed four untracked `bin`/`obj`-only ghost dirs under `src/` (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui`), leftovers from pre-ADR-022/023 refactors. Each verified 0 tracked files first.
- No other untracked cruft existed (`git status` clean before/after).
- **No tracked diff** — the group's only commit (`6684f24`) is the `tasks.md` ticks; the dir removals were untracked, so no reviewer round was needed.
- Baseline established. Note: the worker's report said "1,366 passed" but the true baseline is **1,336 passed / 2 skipped** across 10 assemblies (the 1,366 was a miscount, confirmed by summing per-assembly counts). Use 1,336 as the reference.

## 2. Central Package Management (at current layout)

- Added root `Directory.Packages.props` (`ManagePackageVersionsCentrally=true`), 31 `PackageVersion` + 2 `GlobalPackageReference` (SourceLink, MinVer); stripped inline `Version=` from all 23 csproj. Commit `22ca6b1`.
- **Decision:** SourceLink + MinVer → `GlobalPackageReference` (removed from `Directory.Build.props`) — CPM-idiomatic, implies `PrivateAssets=all`, equivalent behaviour. Rejected leaving them as per-project refs.
- **Decision:** `default-core/` gets a shadowing `Directory.Packages.props` with CPM **off** — its `Dmon.cs` file-based program generates a csproj with floating first-party pins (`@0.2.*`) from the `../.pack-out` local feed, which CPM can't manage. Samples use `VersionOverride` for the same reason. Both are the sanctioned CPM escape hatches, verified minimal by review.
- `CentralPackageFloatingVersionsEnabled=true` needed because **Markdig was already** a floating `1.*` pin (not a loosened fixed version — reviewer confirmed against HEAD).
- Reviewer: zero version drift across all 33 packages; sign-off.

## 3. Bucket moves, reference repair, solutions, and nested props

- `git mv` 13 first-party projects `src/` → `core/ providers/ tools/ middleware/ frontends/` (history preserved: 266 renames). Repaired every `ProjectReference` repo-wide (16 csproj) incl. `extensions/Omlx` inbound ref and the path-coded `test/.../ToolPackTests.cs:175`. Replaced `Dmon.slnx` with 5 area `.slnx` + root `Everything.slnx`. Skew-guard `_ProtocolVersionFile` → `core/Dmon.Protocol/ProtocolVersion.cs`. Commit `8b2c4a3`.
- **Decision (spec refinement):** dropped the per-area `Directory.Build.props` requirement for Phase 0. With no satellites there are no area deltas, so empty import-only props would be pure churn + a real chain-break risk (an area props that forgets to import root silently drops the skew-guard). Amended `specs/monorepo-layout/spec.md` ("Nested build configuration") and task 3.7 to: root props apply repo-wide; per-area props arrive *with* the first delta and must chain-import root. ADR-025 D8 describes the end-state, not a Phase-0 mandate.
- **Decision:** tests stay under top-level `test/` (honors the recorded project convention), not co-located into buckets. Area `.slnx` files reference their area's tests from `test/`; cross-area deps resolve transitively via ProjectReference.
- **Gate deviation (intentional):** `make` is broken after this group (Makefile still points at the deleted `Dmon.slnx` — Makefile/CI/`default-core` are Group 5). So the Group 3 gate ran via **direct `dotnet`** (`dotnet build Everything.slnx` + all 5 area solutions clean; `dotnet test Everything.slnx` = 1,336/2/0; `dotnet pack core/Dmon.Core` ok). `make` is restored and re-gated in Group 5.
- **Scope creep (kept):** worker also path-repaired `scripts/pack-core.sh`, `smoke-cache.sh`, `smoke-sdk.sh` (Group 5 task 5.2). Reviewer verified correct + complete and recommended keeping them (revert = pure churn). → **Group 5 task 5.2 is partially done** (scripts ✓; `default-core/Dmon.cs` still pending).
- Reviewer: Approve. All refs resolve, no `src/` leftovers, builds + pack re-verified.

## 4. Omlx provider relocate and rename

- `git mv extensions/Dmon.Extensions.Omlx → providers/Dmon.Providers.Omlx` (6 source files) and `test/Dmon.Extensions.Omlx.Tests → test/Dmon.Providers.Omlx.Tests` (5 files); all 11 staged as `renamed:` (history preserved). Test csproj `ProjectReference` repaired to `..\..\providers\Dmon.Providers.Omlx\Dmon.Providers.Omlx.csproj`.
- Provider csproj: `AssemblyName`/`RootNamespace`/`PackageId` all = `Dmon.Providers.Omlx`; flipped `IsPackable=true`; added `MinVerTagPrefix=sdk-` + `Description` mirroring sibling packable providers; `InternalsVisibleTo` → `Dmon.Providers.Omlx.Tests`. (`RootNamespace` is mandated by task 4.2, hence present even though Anthropic/OpenAI omit it — not a deviation.)
- Namespace/using rewrite across all 5 provider `.cs` + 4 test `.cs`: `Dmon.Extensions.Omlx` → `Dmon.Providers.Omlx`. Reviewer diffed every moved file against HEAD: **only** namespace/using lines changed — no logic/body edits (pure rename, behaviour preserved).
- slnx wiring: Omlx provider + test added to `providers.slnx` (`/providers/`, `/test/`) and `Everything.slnx`; old `/extensions/` folder block and old test entry removed from `Everything.slnx`. `extensions/` is now empty (untracked) — left as-is.
- Reviewer: Approve, no blockers. Independently confirmed build 0W/0E and Omlx tests 41/41.
- Gates (direct `dotnet` — make still broken until Group 5): `dotnet build Everything.slnx -c Release` 0W/0E; full `dotnet test Everything.slnx -c Release` all green (Omlx 41/41); repo-wide grep for `Dmon.Extensions.Omlx` (excl. bin/obj) returns nothing; `openspec validate --strict` valid. Commit pending.

## 5. Tooling and CI

- `Makefile` repaths: `build-terminal` `src/Dmon.Terminal` → `frontends/Dmon.Terminal`; `build-memory` `src/Dmon.Memory` → `middleware/Dmon.Memory`; `schema` `src/Dmon.Protocol.SchemaGen` → `core/Dmon.Protocol.SchemaGen`.
- **`test` target fix (required):** was `dotnet test -c $(CONFIG)` with no solution arg — now ambiguous because 6 `.slnx` files sit at repo root (`dotnet test` errors on >1 solution). Changed to `dotnet test Everything.slnx -c $(CONFIG)`. This is the line that makes `make test` "across Everything.slnx" (task 6.1).
- **Removed the dead `build-extensions` target** entirely (from `.PHONY`, the `build:` aggregate, the recipe, and the `EXTENSIONS_OUT := build/extensions` var). Rationale verified before removal: it iterated `extensions/*/*.csproj`, but `extensions/` is empty post-Group-4 (Omlx → `providers/` as a package), `build/extensions` is consumed by **nothing** (grep: only self-referenced in the Makefile), and the empty glob would *break* `make build`. Providers/tools/middleware ship as packages via `#:package`, not published into `build/` — consistent with ADR-019/023.
- `.github/workflows/release.yml` pack paths repaired: Protocol/Abstractions/Core `src/` → `core/`, Terminal → `frontends/`. **Removed the `dotnet pack src/Dmon.Extensions/...` line** — that project was deleted under ADR-022, so the line had been broken since; the `sdk-*` line now packs just Protocol + Abstractions. Tag matrix/triggers left untouched (ADR-024 per-package tags + release matrix are explicit Non-Goals).
- `ci.yml` unchanged (drives everything through `make`, no path refs). `scripts/*.sh` + `default-core/Dmon.cs` confirmed clean — already repaired in Group 3 / package-based, **no `src/` refs to fix** (corrects the earlier NEXT note that flagged `default-core/Dmon.cs` as pending — it never had src paths).
- Reviewer: Approve, no blockers. Independently confirmed `make build` 0W/0E, `make test` 0 failed (2 pre-existing skips: Nomic embedding + extension-source-fetcher network integration), `make pack` OK (dmoncore 0.2.0 + contracts + providers, MinVer/skew-guard intact). Note: a test *assembly* named `Dmon.Extensions.Tests.dll` still appears in test output — that's a display name, not the deleted project.
- Gates (orchestrator re-ran): `make build` 0W/0E; `make test` green across `Everything.slnx`; stale-path grep (`src/`/`extensions/`/`Dmon.Extensions` in tooling files) clean. Commit pending.

## NEXT

- **Up next:** Group 6 — final verification gates (`make build`/`make test` green — already passing; assert no intra-repo `PackageReference` to a first-party project; `openspec validate --strict`). (Makefile → `Everything.slnx`/bucket paths; `default-core/Dmon.cs` build-run paths; `.github/workflows/release.yml` lines ~56-67 still have `src/`; finish task 5.2). Group 6 — final gates (`make build`/`make test` green once Makefile restored, no intra-repo PackageReference, `openspec validate --strict`).
- **Carry-forward:**
  - `make` is intentionally red between Group 3 and Group 5 — don't treat it as a regression.
  - Remaining `src/` references live only in `Makefile` (lines 33/49/67) and `.github/workflows/release.yml` (~56-67) — both Group 5.
  - `Dmon.sln.DotSettings` / `.user` still name the old slnx — non-blocking; repoint or leave in Group 5.
  - `Everything.slnx` includes `spike/ScriptingSpike` — fine as a superset; flag for a later phase whether the spike belongs in the everything-build surface.
- **Branch state:** on `change/monorepo-phase0-reorg` (off `main`); ADR-024/025 already merged to `main`. Commits so far: `6684f24` (G1), `22ca6b1` (G2), `8b2c4a3` (G3). Nothing pushed.
- **Open questions:** none blocking.
