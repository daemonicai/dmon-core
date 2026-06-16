## Why

ADR-025 commits dmon-core to becoming the seed of a monorepo, but its layout is still the pre-monorepo flat `src/` + `extensions/` shape, and it carries stale cruft (four untracked ghost project dirs, a misfiled/misnamed Omlx provider). Before any satellite repo can be grafted in — and before git-worktree-per-area parallel work is usable (ADR-025 D7) — the existing projects must be reorganised into the top-level bucket structure with per-area solutions. This is Phase 0: a purely structural move, no behaviour change, that establishes the skeleton everything else hangs off.

## What Changes

- Create top-level **bucket directories** and `git mv` the 13 in-solution projects + their test projects into them: `core/`, `providers/`, `tools/`, `middleware/`, `frontends/` (`samples/` already exists). History-following moves.
- **BREAKING (paths only):** relocate and rename `extensions/Dmon.Extensions.Omlx` → `providers/Dmon.Providers.Omlx` (namespace, assembly, `PackageId` per ADR-023 D3 naming family) and make it packable. Move its test project too.
- Sweep the **four untracked ghost dirs** under `src/` (`Dmon.Extensions`, `Dmon.Providers`, `Dmon.BuiltinTools`, `Dmon.Tui` — `bin/obj`-only leftovers) and other stale build cruft.
- Replace the single `Dmon.slnx` with **per-area solutions** (`core.slnx`, `providers.slnx`, `tools.slnx`, `middleware.slnx`, `frontends.slnx`) **plus** a root `Everything.slnx` superset.
- Introduce **nested `Directory.Build.props` / `Directory.Packages.props`**: a root file with shared settings + per-area files that `Import` the root for area deltas; adopt **central package management** for the version pins currently inline in csprojs.
- Fix the root props' hard-coded skew-guard path `src/Dmon.Protocol/ProtocolVersion.cs` → `core/Dmon.Protocol/ProtocolVersion.cs`.
- Update `Makefile`, `scripts/`, `default-core/Dmon.cs` build/run paths, and the existing GitHub Actions workflow(s) so build/test/pack stay green on the new layout.
- Keep all intra-repo references as **`ProjectReference`** (ADR-025 D4). No new features, no versioning-scheme changes.

## Capabilities

### New Capabilities
- `monorepo-layout`: the standing structural contract for the repository — top-level role buckets and what each holds, the per-area `.slnx` + root `Everything.slnx` requirement, the intra-repo `ProjectReference` rule, the nested `Directory.Build.props`/`Directory.Packages.props` arrangement, and the ADR-023 D3 package naming families. Future changes (satellite grafts, new packages) must conform to it.

### Modified Capabilities
<!-- None. This change relocates files and build plumbing; it does not change any spec-level behaviour of agent-core, protocol-schema, package-publishing, provider-extension, or any other existing capability. -->

## Impact

- **Every project path** in the repo (all 14 source projects + 10 test projects) moves; all `.csproj`/`.slnx`/`ProjectReference` paths, `Directory.*.props` locations, `Makefile`, `scripts/`, `default-core/Dmon.cs`, and CI workflow paths are affected.
- **Omlx consumers** (the `extensions/` project and its tests) change namespace/assembly/PackageId.
- **Build/packaging plumbing**: skew-guard path, central package management adoption.
- **No runtime, RPC, protocol, or session-storage behaviour changes.** Gates: `make build` clean (TreatWarningsAsErrors), `make test` green (all existing tests), ADR-011 protocol skew-guard intact, `openspec validate monorepo-phase0-reorg --strict`.
- **Out of scope** (later phases): importing `dmon-llama-cpp`/`dmail`/`dmon-meko`/`dmon-websearch`; `dcli` (external); `dmonium`; ADR-024 per-package tag prefixes / skew-guard patch-relax; the release matrix; full dependency-aware path-filtered CI (defined as a fast-follow in design.md).
