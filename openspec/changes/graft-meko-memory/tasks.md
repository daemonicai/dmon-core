## 1. Promote memory to the `memory/` bucket (in-tree move)

- [x] 1.1 `git mv middleware/Dmon.Memory memory/Dmon.Memory` (history-preserving); confirm `git log --follow memory/Dmon.Memory/Facade/Memory.cs` shows pre-move history.
- [x] 1.2 If a `Dmon.Memory` test project exists under `test/`, leave it in place (`test/` convention); fix its `ProjectReference` path to `..\..\memory\Dmon.Memory\Dmon.Memory.csproj`.
- [x] 1.3 Fix `Dmon.Memory`'s own `ProjectReference`s (to `core/Dmon.Abstractions`, `core/Dmon.Core`, etc.) for the new depth; confirm no inline first-party `PackageReference` crept in.
- [x] 1.4 Create `memory.slnx` (mirror a sibling area `.slnx`) containing `memory/Dmon.Memory` under `/memory/` (or `/src/`-style folder per the slnx convention) and its test under `/test/`.
- [x] 1.5 Remove `middleware.slnx` and the now-empty `middleware/` directory; grep the repo (excl. `bin/obj`) for `middleware/` and `middleware.slnx` references and fix/remove them.
- [x] 1.6 Repath `Everything.slnx` (`middleware/Dmon.Memory` → `memory/Dmon.Memory`) and the `Makefile` (any `middleware`/area references).
- [x] 1.7 Gate (Group 1 standalone): `make build` clean, `make test` green, `dotnet build memory.slnx -c Release` clean, `openspec validate graft-meko-memory --strict`.

## 2. History-preserving import of the dmon-meko long-term tier

- [ ] 2.1 Ensure `git-filter-repo` is available (`uvx git-filter-repo --version`).
- [ ] 2.2 `git clone --no-local -b main /Users/rendle/github/daemonicai/dmon-meko /tmp/graft-meko` (source `main` @ `8ed0886`, grafted as-is per design D6); never operate on the original repo. (`--no-local` + direct `-b main` are required so filter-repo sees a fresh, single-reflog clone.)
- [ ] 2.3 Run `uvx git-filter-repo` keeping only `src/Dmon.Memory.Meko/` and `test/Dmon.Memory.Meko.Tests/`, with `--path-rename`s to `memory/Dmon.Memory.Meko/` and `test/Dmon.Memory.Meko.Tests/`.
- [ ] 2.4 On branch `change/graft-meko-memory`, add the throwaway clone as a remote, `git merge --allow-unrelated-histories meko-graft/main`, then remove the remote and delete `/tmp/graft-meko`.
- [ ] 2.5 Confirm only the intended paths landed (the two subtrees incl. the package `README.md`); no satellite `openspec/`, `nuget.config`, vendored nupkgs, `Directory.*` files, or the `add-memory-abstraction` change imported.

## 3. Re-wire the grafted package to monorepo conventions

- [ ] 3.1 `Dmon.Memory.Meko.csproj`: replace the `Dmon.Abstractions` `PackageReference` with a `ProjectReference` to `..\..\core\Dmon.Abstractions\Dmon.Abstractions.csproj`; audit `using`s and add an explicit `core/Dmon.Protocol` `ProjectReference` only if `Dmon.Protocol.*` types are used directly.
- [ ] 3.2 Move third-party pins to root CPM `Directory.Packages.props` (add `<PackageVersion>` for genuinely new ones — e.g. `ModelContextProtocol`; reuse existing pins for `Microsoft.Extensions.AI.Abstractions` and the `Microsoft.Extensions.*` abstractions); strip inline `Version=` from the grafted `.csproj`.
- [ ] 3.3 `.csproj` packable hygiene: `IsPackable=true`, MinVer tag prefix, packed `README.md`, no inline `<Version>`/`<Authors>`; ensure the skew-guard target + `core/Dmon.Protocol/ProtocolVersion.cs` path apply.
- [ ] 3.4 Test project: conform to the repo test convention (xunit, CPM-bare pins); if the imported test csproj diverges, scaffold a fresh `test/Dmon.Memory.Meko.Tests/Dmon.Memory.Meko.Tests.csproj` from a sibling (`test/Dmon.Memory.Tests`) referencing only `memory/Dmon.Memory.Meko`, keeping the imported test source. Confirm the live smoke test stays `Category=Live`-gated (skipped without `MEKO_API_KEY`).
- [ ] 3.5 Repo-wide grep (excl. `bin/obj`, incl. `*.md`): no stale `Version=` inline pins, no `Dmon.Middleware` rename tokens, no broken `#:package`/namespace references in the grafted README.

## 4. Solutions

- [ ] 4.1 Add `memory/Dmon.Memory.Meko/Dmon.Memory.Meko.csproj` to `memory.slnx` (under `/memory/`) and `test/Dmon.Memory.Meko.Tests` (under `/test/`).
- [ ] 4.2 Add both projects to `Everything.slnx`.

## 5. Verification gates

- [ ] 5.1 `dotnet build Everything.slnx -c Release` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 5.2 `make build` and `make test` green — the imported Meko tests (offline, fake invoker) plus all existing tests; live smoke test skipped.
- [ ] 5.3 `dotnet pack memory/Dmon.Memory.Meko/Dmon.Memory.Meko.csproj -c Release` succeeds — skew-guard passes and a sane MinVer `Major.Minor` matching `core/Dmon.Protocol/ProtocolVersion.cs` is produced; repeat for `memory/Dmon.Memory`.
- [ ] 5.4 `git log --follow memory/Dmon.Memory.Meko/Meko/MekoLongTermMemory.cs` shows pre-graft history (import preserved); `git log --follow memory/Dmon.Memory/Facade/Memory.cs` shows pre-move history.
- [ ] 5.5 `openspec validate graft-meko-memory --strict` passes (the `monorepo-layout` delta and the new `memory-meko` capability).
