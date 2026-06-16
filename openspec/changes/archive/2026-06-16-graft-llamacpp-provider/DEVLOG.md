# DEVLOG: graft-llamacpp-provider

<!-- ADR-025 Phase 1: graft the local-only dmon-llama-cpp satellite into the monorepo as providers/Dmon.Providers.LlamaCpp. Resolves ADR-025's open import-mechanics question. -->

## 1. History-preserving import

- Acquired `git filter-repo` ad hoc via `uvx git-filter-repo` (not installed in-repo). **Gotcha:** a local-path `git clone` uses hardlinks, which filter-repo rejects as "not a fresh clone" — re-cloned with `git clone --no-local /Users/rendle/github/daemonicai/dmon-llama-cpp /tmp/graft-llamacpp`.
- In the clone, `git filter-repo --path src/Dmon.Extensions.LlamaCpp/ --path test/Dmon.Extensions.LlamaCpp.Tests/ --path-rename …:providers/Dmon.Providers.LlamaCpp/ --path-rename …:test/Dmon.Providers.LlamaCpp.Tests/` — kept ONLY src+test (dropped the satellite's `openspec/`, `nuget.config`, vendored nupkgs, root README/LICENSE, CI, `Directory.*`), remapped to bucket paths, 6 commits of history preserved.
- Merged into `change/graft-llamacpp-provider` via a remote + `git merge --allow-unrelated-histories` (commit `bd8bcdc`), then removed the remote and deleted the clone. History verified: `git log --follow providers/Dmon.Providers.LlamaCpp/LlamaCppProviderExtension.cs` shows the pre-graft commits (`337bbcf`/`ee72229`/`6be9b57`/`954de6a`). Original `dmon-llama-cpp` left untouched. (Group-1 ticks committed `ccb6e4d`; paused for user review here.)
- **Recipe established (resolves ADR-025's open import-mechanics question):** `uvx git-filter-repo` on a `--no-local` clone, `--path`+`--path-rename` to the bucket split, `merge --allow-unrelated-histories`. Reusable by the remaining satellites.

## 2–4. Rename + re-wire + solutions (one integration unit; build only goes green once all done)

- Rename to `Dmon.Providers.LlamaCpp` (ADR-023 D3), mirroring Phase-0 Omlx: `git mv` both csprojs to the new names; `AssemblyName`/`RootNamespace`/`PackageId` = `Dmon.Providers.LlamaCpp`; `namespace`/`using`/`InternalsVisibleTo` rewritten across 4 src + verb + 2 test files. Source bodies byte-identical apart from namespace/using (reviewer-verified — no behaviour change).
- ProjectReference: `PackageReference Dmon.Abstractions 0.2.*` → `ProjectReference` to `core/Dmon.Abstractions` (ADR-025 D4); test project repointed. Standalone `<Version>` removed (MinVer drives via `MinVerTagPrefix sdk-`); all inline `Version=` stripped.
- **CPM:** `Directory.Packages.props` needed **no change** — `Microsoft.Extensions.AI.OpenAI` (10.5.1, same line as `Microsoft.Extensions.AI`, used by the OpenAI provider) and all test deps were already centrally pinned from Phase 0, so the grafted projects just reuse them (no duplicates, no NU1010, no split-brain AI version).
- Wired both projects into `providers.slnx` (`/providers/` + `/test/`) and `Everything.slnx`.
- **Reviewer round 1 → changes-needed (blocker B1):** the provider `README.md` is the shipped NuGet `PackageReadmeFile` but still had `# Dmon.Extensions.LlamaCpp`, stale `.slnx`/`src/` build-test-pack commands, and satellite-era local-feed/`nuget.config` guidance — all of which ship to consumers. (My code-only grep had missed it by not scanning `.md`.) Worker fixed the README to the monorepo story (title, valid `Everything.slnx`/`providers.slnx`/provider-csproj commands, transitive-dependency note, no `dmon-llama-cpp` URLs); usage docs preserved. **Reviewer round 2 → sign-off.**

## 5. Verification gates

- `dotnet build Everything.slnx -c Release` 0W/0E; `make build` clean; full `dotnet test Everything.slnx -c Release` green (LlamaCpp tests 29/29; 2 pre-existing skips repo-wide).
- `dotnet pack providers/Dmon.Providers.LlamaCpp` → `Dmon.Providers.LlamaCpp.0.2.0-alpha.0.35.nupkg` (+ `.snupkg`) — MinVer + protocol skew-guard (`core/Dmon.Protocol/ProtocolVersion.cs`, 0.2) intact.
- 0 intra-repo first-party `PackageReference` in the grafted projects (Dmon.Abstractions is a ProjectReference). Repo-wide grep for `Dmon.Extensions.LlamaCpp` (excl bin/obj + this change's own docs) clean. `openspec validate graft-llamacpp-provider --strict` valid.
- **5.6 source disposition:** `dmon-llama-cpp` is local-only (no GitHub repo) → tagged `absorbed-into-dmon-core` on its `main` (@ `f9c6c4d`) and recorded as read-only/historical; repo left intact.

## DONE

- All 19 tasks complete. Commits: `86e0bbb` propose · `bd8bcdc` import · `ccb6e4d` G1 tick · `bb8fdb0` integration (G2–5). **MERGED via PR #46** (merge commit `d12fd47`) and **ARCHIVED** here; standing spec synced to `openspec/specs/llamacpp-provider/` (archive commit `2357db3` on `main`). `dmon-llama-cpp` tagged `absorbed-into-dmon-core`, left intact.
- **Recipe proven (resolves ADR-025 import-mechanics):** `uvx git-filter-repo` on a `git clone --no-local` clone → `--path`/`--path-rename` to bucket split → `merge --allow-unrelated-histories` → rename + PackageRef→ProjectRef + CPM + slnx + fix the shipped README (grep `.md` too).
- **Next satellite:** **dmail** (consolidated via PR #3) — its graft additionally needs the heavier `IDmonExtension`→`IToolExtension` / `Dmon.Extensions`→`Dmon.Abstractions` API port done inside the graft change. Then dmon-meko (verify-then-graft); dmon-websearch is empty (greenfield).
