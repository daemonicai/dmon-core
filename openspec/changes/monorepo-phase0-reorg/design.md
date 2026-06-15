## Context

dmon-core today is a flat `src/` (13 in-`.slnx` projects) + `extensions/Dmon.Extensions.Omlx`, one `Dmon.slnx`, a single root `Directory.Build.props` with inline package pins, tests under `test/`, and four untracked ghost project dirs left by earlier refactors. ADR-025 makes this repo the monorepo seed; Phase 0 reshapes it into the bucket layout (Decisions 2,3,4,8,13) before any satellite is grafted. The hard constraint is that **CI stays green** and **no behaviour changes** — this is pure relocation + build plumbing.

## Goals / Non-Goals

**Goals:**
- Projects sorted into `core/ providers/ tools/ middleware/ frontends/` (+ existing `samples/`), history preserved via `git mv`.
- `Dmon.slnx` replaced by per-area `.slnx` + a root `Everything.slnx`.
- Nested `Directory.Build.props` + central `Directory.Packages.props`.
- Omlx relocated/renamed to `Dmon.Providers.Omlx` (packable).
- Ghost dirs and stale cruft removed.
- `make build`/`make test`/pack and the existing CI workflow green on the new layout.

**Non-Goals:**
- Importing any satellite repo (`dmon-llama-cpp`, `dmail`, `dmon-meko`, `dmon-websearch`), `dcli` (external), or `dmonium` — later phases.
- ADR-024 versioning tooling (per-package tag prefixes, skew-guard patch-relax) — separate change.
- The two-family release matrix.
- Full dependency-aware path-filtered CI ("core ⇒ Everything") — defined as a fast-follow (Decision: CI).
- Moving test projects into buckets (see Decision: Tests).

## Decisions

**Bucket placement.** `core/` ← Abstractions, Protocol, Core (dmoncore), Runtime, Protocol.SchemaGen. `providers/` ← Anthropic, OpenAI, Gemini, Ollama, Omlx. `tools/` ← Tools.Builtin. `middleware/` ← Memory. `frontends/` ← Terminal, Gateway. Each project keeps its own directory; only the parent path changes. *Alternative considered:* group by "ships to nuget vs internal" — rejected (ADR-025 chose role buckets).

**Tests stay under top-level `test/`.** Honors the recorded project convention (test projects live under `test/`). Per-area `.slnx` files reference their area's test projects from `test/`; `Everything.slnx` includes all of `test/`. *Alternative considered:* co-locate tests inside each bucket — deferred; would contradict the standing convention and adds churn with no Phase-0 benefit.

**History-following moves.** Use `git mv` for every tracked project so blame/log survive. The four ghost dirs are *untracked* (`bin/obj` only) — they are removed with a plain `rm -rf`, not `git mv`, and are not part of the commit's tracked diff.

**ProjectReference path repair.** Moving a project changes the relative depth of every `<ProjectReference Include="..\..\X\X.csproj">`. After the `git mv` wave, all `ProjectReference` paths are rewritten to the new relative locations. Solutions are regenerated (`dotnet sln` / hand-edited `.slnx`) to point at new paths. This is the highest-risk mechanical step; it is verified by a clean `Everything.slnx` restore+build.

**Central Package Management.** Add a root `Directory.Packages.props` with `ManagePackageVersionsCentrally=true`; move the inline `Version=` from every `PackageReference` into `<PackageVersion>` entries; leave build-time/`PrivateAssets` refs (SourceLink, MinVer) where ADR-conformant. *Alternative considered:* keep inline pins — rejected; central management is the point of nested props and prevents version drift across buckets.

**Nested props.** Root `Directory.Build.props` keeps the shared block (Authors, MinVer, SourceLink, symbol packages, skew-guard, `IsPackable=false` default). Each area gets a `Directory.Build.props` that `<Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />` (or equivalent) then adds only area deltas. MSBuild already auto-imports the nearest `Directory.Build.props` upward, so area files must explicitly chain to the root.

**Skew-guard path.** Update `_ProtocolVersionFile` to `core/Dmon.Protocol/ProtocolVersion.cs`. The regex and single-line `Current = "x.y"` constraint are unchanged.

**Omlx rename.** `git mv extensions/Dmon.Extensions.Omlx providers/Dmon.Providers.Omlx`; rename the `.csproj`; set `AssemblyName`/`RootNamespace`/`PackageId` to `Dmon.Providers.Omlx`; flip to packable; update `namespace`/`using` in its `.cs` and its test project. Verify nothing else referenced the old assembly name.

**CI.** Update the existing workflow's paths/solution references so build+test+pack run against `Everything.slnx` (or the per-area solutions) on the new layout — green is the bar. Dependency-aware path filtering ("core change ⇒ Everything, else area-only") is explicitly a **fast-follow change**, not Phase 0, to keep this change purely structural.

## Risks / Trade-offs

- **Broken `ProjectReference` relative paths after moves** → rewrite all paths in the same commit; gate on a clean `Everything.slnx` build before ticking anything.
- **MinVer/skew-guard regression** (path or CPM interaction) → explicitly build a packable project and assert the guard still fires/passes; keep the single-line `Current` const.
- **Central Package Management misses a transitive pin or trips `NU1010`** → build the full solution; resolve any "version not centrally defined" errors before commit.
- **`default-core/Dmon.cs` build/run path drift** → it consumes packages via `#:package`, not project refs, so only its build/run *script paths* change; update `Makefile`/`scripts/` accordingly and smoke-run it.
- **Editor/solution-folder churn** (`Dmon.sln.DotSettings`) → regenerate or repoint; non-blocking.
- **Large single diff is hard to review** → group the work (see tasks.md) so each group builds green independently, even though the rename wave is necessarily one commit.

## Migration Plan

1. Buckets + `git mv` projects (core → providers → tools → middleware → frontends), rewrite `ProjectReference` paths.
2. Remove ghost dirs + cruft.
3. Omlx relocate/rename.
4. Solutions: delete `Dmon.slnx`; create per-area `.slnx` + `Everything.slnx`.
5. Props: root + per-area `Directory.Build.props`, `Directory.Packages.props` (CPM); fix skew-guard path.
6. Tooling: `Makefile`, `scripts/`, `default-core/Dmon.cs` paths, CI workflow.
7. Gates: `make build` clean, `make test` green, pack a packable project, `openspec validate --strict`.

Rollback: the change is one branch; revert the branch. No data or wire-protocol surface is touched.

## Open Questions

- None blocking. (Test co-location and full path-filtered CI are deliberately deferred, not unresolved.)
