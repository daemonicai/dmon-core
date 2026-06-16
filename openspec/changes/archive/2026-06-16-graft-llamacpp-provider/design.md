## Context

Phase 0 reshaped `dmon-core` into the ADR-025 bucket layout (`core/ providers/ tools/ middleware/ frontends/`, per-area `.slnx` + `Everything.slnx`, central package management, MinVer + protocol skew-guard in the root `Directory.Build.props`). `dmon-llama-cpp` is a separate, **local-only** repo (no GitHub remote) whose provider work is complete and consolidated on its own `main` (@ `f9c6c4d`): a managed local llama.cpp provider that owns a `llama-server` subprocess and exposes it as an `IChatClient`, with the `UseLlamaCpp` verb already written against the current `IProviderRegistration` API. Its layout is `src/Dmon.Extensions.LlamaCpp/` + `test/Dmon.Extensions.LlamaCpp.Tests/`, referencing the published `Dmon.Abstractions` SDK via `PackageReference` (resolved from vendored nupkgs).

This change imports that provider into the monorepo and integrates it to the same shape Phase 0 applied to the already-in-tree Omlx provider. ADR-025 left **import mechanics** as an open question; this change settles them and is the template for the remaining satellite grafts.

## Goals / Non-Goals

**Goals:**
- Import `dmon-llama-cpp`'s code **with history** into `providers/Dmon.Providers.LlamaCpp/` and `test/Dmon.Providers.LlamaCpp.Tests/`.
- Rename to the ADR-023 D3 provider family `Dmon.Providers.LlamaCpp` (assembly/namespace/PackageId/dirs).
- Re-wire to monorepo conventions: `Dmon.Abstractions` `ProjectReference` (ADR-025 D4), central package management, MinVer + skew-guard from root props, packable like sibling providers, in `providers.slnx` + `Everything.slnx`.
- Establish a **repeatable graft recipe** (the open ADR-025 question) the next satellites reuse.
- All gates green: `Everything.slnx` build, `make build`/`make test`, `dotnet pack` of the provider, `openspec validate --strict`.

**Non-Goals:**
- The **dmail** graft (needs an additional `IDmonExtension`→`IToolExtension` / `Dmon.Extensions`→`Dmon.Abstractions` API port) — separate change.
- Dependency-aware path-filtered CI, the two-family release matrix, ADR-024 per-package tag prefixes / skew-guard patch-relax — later, per Phase 0.
- Any behaviour change to the provider or to existing dmon-core code.
- Importing the satellite's own `openspec/` (its archived change + standing spec) — bookkeeping; the capability is (re)authored here as a spec delta.

## Decisions

**Import tool: `git filter-repo` on a throwaway clone.** `git-filter-repo` is not installed; acquire it ad hoc with **`uvx git-filter-repo`** (uv is available) — fallback `brew install git-filter-repo`. filter-repo refuses to rewrite a repo that still has its origin/working changes, so operate on a **fresh clone**, never the original repo:
```
git clone /Users/rendle/github/daemonicai/dmon-llama-cpp /tmp/graft-llamacpp   # local-only source
cd /tmp/graft-llamacpp                                                          # on its main @ f9c6c4d
uvx git-filter-repo \
  --path src/Dmon.Extensions.LlamaCpp/ \
  --path test/Dmon.Extensions.LlamaCpp.Tests/ \
  --path-rename src/Dmon.Extensions.LlamaCpp/:providers/Dmon.Providers.LlamaCpp/ \
  --path-rename test/Dmon.Extensions.LlamaCpp.Tests/:test/Dmon.Providers.LlamaCpp.Tests/
```
`--path` keeps **only** the source + test subtrees (dropping the satellite's `openspec/`, root `nuget.config`, vendored nupkgs, root README/LICENSE, CI, `Directory.*` plumbing — none of which should enter the monorepo); the two `--path-rename`s relocate them to the bucket layout **with history**. The directory is renamed by filter-repo; the `.csproj` *filenames*, file contents, and namespaces are still the old `Dmon.Extensions.LlamaCpp` — those are fixed after the merge (next decisions).

**Merge: one-time `--allow-unrelated-histories`.** On the `change/graft-llamacpp-provider` branch:
```
git remote add llamacpp-graft /tmp/graft-llamacpp
git fetch llamacpp-graft
git merge --allow-unrelated-histories llamacpp-graft/main -m "Import dmon-llama-cpp history into providers/ (graft)"
git remote remove llamacpp-graft
```
No conflicts are expected (the imported paths are new to `dmon-core`). History is verifiable post-merge with `git log --follow providers/Dmon.Providers.LlamaCpp/LlamaCppProviderExtension.cs` showing pre-graft commits. The throwaway clone is deleted afterward. The original `dmon-llama-cpp` repo is left **intact and untouched** until the graft is merged and verified.

**Rename to the provider family (mirrors Phase 0 Omlx).** After the merge: `git mv` the provider `.csproj` and the test `.csproj` to their new names; set `AssemblyName`/`RootNamespace`/`PackageId` = `Dmon.Providers.LlamaCpp`; rewrite `namespace Dmon.Extensions.LlamaCpp` → `Dmon.Providers.LlamaCpp` across the 4 src files (`LlamaCppOptions`, `LlamaCppProviderExtension`, `LlamaCppProviderFactory`, `LlamaCppRuntimeState`) and `Dmon.Extensions.LlamaCpp.Tests` → `Dmon.Providers.LlamaCpp.Tests` across the test files; update the `UseLlamaCppExtensions.cs` `using Dmon.Extensions.LlamaCpp;` and `InternalsVisibleTo` to the new names. A repo-wide grep for `Dmon.Extensions.LlamaCpp` (excluding `bin/obj`) must return nothing.

**Intra-repo `ProjectReference` (ADR-025 D4).** Provider `.csproj`: replace `<PackageReference Include="Dmon.Abstractions" Version="0.2.*" />` with `<ProjectReference Include="..\..\core\Dmon.Abstractions\Dmon.Abstractions.csproj" />`. Test `.csproj`: repoint its `ProjectReference` to `..\..\providers\Dmon.Providers.LlamaCpp\Dmon.Providers.LlamaCpp.csproj`.

**Central Package Management.** Remove the provider's standalone `<Version>0.x</Version>` (MinVer drives the version) and drop inline `Version=` from all `PackageReference`s; add any missing third-party pins to the root `Directory.Packages.props` as `<PackageVersion>` — notably `Microsoft.Extensions.AI.OpenAI` (align its version to the existing `Microsoft.Extensions.AI` line already pinned for the repo, not the satellite's stray `10.5.1` unless that *is* the repo line) and the test project's `Microsoft.Extensions.Configuration` / `Microsoft.Extensions.DependencyInjection` (most test deps — xunit, coverlet, Test.Sdk, runner, skippablefact — are already centrally pinned from Phase 0; reuse those, don't duplicate). The provider keeps `IsPackable=true`, `MinVerTagPrefix` and a `Description` consistent with sibling providers (e.g. Omlx). Nothing from the satellite's own `nuget.config` / `Directory.Packages.props` is imported (excluded at filter-repo time), so there is no feed plumbing to delete.

**Solutions.** Add `providers/Dmon.Providers.LlamaCpp/Dmon.Providers.LlamaCpp.csproj` to `providers.slnx` (under `/providers/`) and its test under `/test/`; add both to `Everything.slnx`. The per-area `core.slnx` etc. are unaffected.

**OpenSpec capability provenance.** The `llamacpp-provider` capability is authored as **this change's spec delta** (`specs/llamacpp-provider/spec.md`, all `ADDED`), with content lifted from the satellite's standing spec. On archive it syncs to `openspec/specs/llamacpp-provider/` — the same provenance pattern Phase 0 used for `monorepo-layout`, and consistent with the other root-level provider specs (`ollama-provider`, `omlx-provider`). The satellite's `openspec/` is deliberately **not** imported, so the capability has a clean change history in the monorepo rather than appearing without provenance.

**Source-repo disposition.** `dmon-llama-cpp` is local-only (no GitHub repo to archive). After the graft merges, record it as **absorbed/read-only** (a note in this change's DEVLOG + memory; optionally a local `absorbed-into-dmon-core` git tag on its `main`). Do not delete it.

**Versioning.** No ADR-024 tooling here. The provider inherits the monorepo's MinVer + protocol skew-guard via the root `Directory.Build.props`; `Major.Minor` must match `core/Dmon.Protocol/ProtocolVersion.cs` — verified by packing the provider.

## Risks / Trade-offs

- **`git filter-repo` unavailable / refuses to run** → use `uvx git-filter-repo` on a fresh clone (filter-repo's safety checks pass on a clean clone). If `uvx` can't fetch it, `brew install git-filter-repo`. Gate the merge on a successful filtered clone.
- **CPM version clash for `Microsoft.Extensions.AI.OpenAI`** (satellite pinned `10.5.1`; repo may pin the AI line differently) → resolve to the repo's existing AI-line version in `Directory.Packages.props`; build the full solution to surface any `NU1605`/downgrade. Trade-off accepted: the provider tracks the monorepo's AI version, not its own.
- **Skew-guard / MinVer regression on pack** (path or CPM interaction) → explicitly `dotnet pack` the provider and assert the guard passes and a sane MinVer version is produced; keep `core/Dmon.Protocol/ProtocolVersion.cs` single-line.
- **History not actually preserved** (wrong path-rename) → verify with `git log --follow` on a renamed source file before ticking; the rename wave must be `git mv` so blame survives the second hop.
- **Large unrelated-history merge is awkward to review** → keep the import as its own commit, then the rename/reref/CPM/slnx integration as the following commit(s); each must leave `Everything.slnx` building green.
- **Stale `Dmon.Extensions.LlamaCpp` references** anywhere (csproj filename, namespaces, slnx, InternalsVisibleTo) → final repo-wide grep gate (excluding `bin/obj`) returns nothing.

## Open Questions

- None blocking. (Per-area `openspec/` roots for grafted single-package specs — ADR-025's hybrid-root end state — are not yet instantiated; until they are, provider specs live at root `openspec/specs/` consistent with `ollama-provider`/`omlx-provider`. Revisit when the hybrid roots are stood up.)
