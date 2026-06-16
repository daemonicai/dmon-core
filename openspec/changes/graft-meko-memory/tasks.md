## 1. Promote memory to the `memory/` bucket (in-tree move)

- [x] 1.1 `git mv middleware/Dmon.Memory memory/Dmon.Memory` (history-preserving); confirm `git log --follow memory/Dmon.Memory/Facade/Memory.cs` shows pre-move history.
- [x] 1.2 If a `Dmon.Memory` test project exists under `test/`, leave it in place (`test/` convention); fix its `ProjectReference` path to `..\..\memory\Dmon.Memory\Dmon.Memory.csproj`.
- [x] 1.3 Fix `Dmon.Memory`'s own `ProjectReference`s (to `core/Dmon.Abstractions`, `core/Dmon.Core`, etc.) for the new depth; confirm no inline first-party `PackageReference` crept in.
- [x] 1.4 Create `memory.slnx` (mirror a sibling area `.slnx`) containing `memory/Dmon.Memory` under `/memory/` (or `/src/`-style folder per the slnx convention) and its test under `/test/`.
- [x] 1.5 Remove `middleware.slnx` and the now-empty `middleware/` directory; grep the repo (excl. `bin/obj`) for `middleware/` and `middleware.slnx` references and fix/remove them.
- [x] 1.6 Repath `Everything.slnx` (`middleware/Dmon.Memory` → `memory/Dmon.Memory`) and the `Makefile` (any `middleware`/area references).
- [x] 1.7 Gate (Group 1 standalone): `make build` clean, `make test` green, `dotnet build memory.slnx -c Release` clean, `openspec validate graft-meko-memory --strict`.

## 2. History-preserving import of the dmon-meko long-term tier

- [x] 2.1 Ensure `git-filter-repo` is available (`uvx git-filter-repo --version`).
- [x] 2.2 `git clone --no-local -b main /Users/rendle/github/daemonicai/dmon-meko /tmp/graft-meko` (source `main` @ `8ed0886`, grafted as-is per design D6); never operate on the original repo. (`--no-local` + direct `-b main` are required so filter-repo sees a fresh, single-reflog clone.)
- [x] 2.3 Run `uvx git-filter-repo` keeping only `src/Dmon.Memory.Meko/` and `test/Dmon.Memory.Meko.Tests/`, with `--path-rename`s to `memory/Dmon.Memory.Meko/` and `test/Dmon.Memory.Meko.Tests/`.
- [x] 2.4 On branch `change/graft-meko-memory`, add the throwaway clone as a remote, `git merge --allow-unrelated-histories meko-graft/main`, then remove the remote and delete `/tmp/graft-meko`.
- [x] 2.5 Confirm only the intended paths landed (23 files: 11 src + 12 test). NB the satellite `README.md` lived at its repo root, not under `src/`, so it was correctly NOT imported — any packed-README reference in the csproj is a Group 3 packaging concern. No satellite `openspec/`, `nuget.config`, vendored nupkgs, `Directory.*` files, or the `add-memory-abstraction` change imported.

## 3. Re-wire the grafted package to monorepo conventions

- [x] 3.1 `Dmon.Memory.Meko.csproj`: replace the `Dmon.Abstractions` `PackageReference` with a `ProjectReference` to `..\..\core\Dmon.Abstractions\Dmon.Abstractions.csproj`; audit `using`s and add an explicit `core/Dmon.Protocol` `ProjectReference` only if `Dmon.Protocol.*` types are used directly. (Protocol ref ADDED — `MessageRecord`/`TextPart` used directly; see 3.x note.)
- [x] 3.2 Move third-party pins to root CPM `Directory.Packages.props` (add `<PackageVersion>` for genuinely new ones — e.g. `ModelContextProtocol`; reuse existing pins for `Microsoft.Extensions.AI.Abstractions` and the `Microsoft.Extensions.*` abstractions); strip inline `Version=` from the grafted `.csproj`. (Added ModelContextProtocol 1.3.0, M.E.AI.Abstractions/Logging.Abstractions/Options/Options.ConfigurationExtensions; bumped M.E.AI family 10.5.1→10.5.2 for MCP 1.3.0 — whole repo re-verified green.)
- [x] 3.3 `.csproj` packable hygiene: `IsPackable=true`, MinVer tag prefix (`sdk-`), packed `README.md` (authored fresh — satellite README lived at repo root, not imported), no inline `<Version>`/`<Authors>` (URLs/license/symbols inherited from root props); skew-guard applies (pack → `0.2.0-alpha.0.42`).
- [x] 3.4 Test project: conform to the repo test convention (xunit, CPM-bare pins) referencing `memory/Dmon.Memory.Meko` (+ `core/Dmon.Protocol`, used directly). Live smoke test stays `Category=Live`-gated (skipped without `MEKO_API_KEY`).
- [x] 3.5 Repo-wide grep (excl. `bin/obj`, incl. `*.md`): no stale `Version=` inline pins, no `Dmon.Middleware` rename tokens (only ADR/spec prose), no broken `#:package`/namespace references.

## 4. Solutions

- [x] 4.1 Add `memory/Dmon.Memory.Meko/Dmon.Memory.Meko.csproj` to `memory.slnx` (under `/memory/`) and `test/Dmon.Memory.Meko.Tests` (under `/test/`).
- [x] 4.2 Add both projects to `Everything.slnx`.

## 5. Verification gates

- [x] 5.1 `dotnet build Everything.slnx -c Release` clean (no warnings; `TreatWarningsAsErrors`).
- [x] 5.2 `make build` and `make test` green — `Dmon.Memory.Meko.Tests` 71 passed (offline, fake invoker) plus all existing suites; live smoke test skipped; 0 failures.
- [x] 5.3 `dotnet pack memory/Dmon.Memory.Meko/Dmon.Memory.Meko.csproj -c Release` succeeds — skew-guard passes, `0.2.0-alpha.0.42` (Major.Minor=0.2=`ProtocolVersion.Current`); `memory/Dmon.Memory` packs likewise.
- [x] 5.4 `git log --follow memory/Dmon.Memory.Meko/Meko/MekoLongTermMemory.cs` shows pre-graft history (`6827681`/`4165a5f`/`f96b4a6`); `git log --follow memory/Dmon.Memory/Facade/Memory.cs` shows pre-move history.
- [x] 5.5 `openspec validate graft-meko-memory --strict` passes (the `monorepo-layout` delta and the new `memory-meko` capability).
