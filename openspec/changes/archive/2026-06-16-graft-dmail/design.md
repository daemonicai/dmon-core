## Context

Phase 1 (`graft-llamacpp-provider`) settled ADR-025's open "import mechanics" question and established the history-preserving graft recipe: `git filter-repo` path-renames on a throwaway clone, a one-time `--allow-unrelated-histories` merge, then rename/re-reference/CPM/slnx integration. This change is the second application of that recipe, targeting the `tools/` bucket — and the first graft that also needs an **API port**, because the source predates ADR-022.

The source is the local-only `dmail` repo. Its dmon **tool extension** lives at `src/Dmon.Extensions.Dmail/` on the `feat/dmon-tool-dmail` branch (@ `b1af562`; the extension is not on `dmail`'s `main`). The extension is a thin HTTP client: it exposes `search_email`, `check_new_messages`, and `get_email` as `AIFunction`s that call the Dmail server's HTTP API, configured from `DMAIL_BASE_URL`/`DMAIL_API_KEY`. It targets the **deleted** `Dmon.Extensions` package (`Dmon.Extensions` namespace, `IDmonExtension`), resolved from vendored nupkgs — ADR-022 collapsed `Dmon.Extensions` into `Dmon.Abstractions` and renamed `IDmonExtension` → `IToolExtension`.

The `dmail` repo also contains a standalone ASP.NET Core email **server** (`src/Dmail/`: IMAP ingestion, embeddings, hybrid vector search, OAuth2, an admin dashboard) and a single test project (`test/Dmail.Tests/`) covering both server and extension. The server is **not** grafted (no bucket fits a standalone service); it stays in `dmail` as the deployable the extension targets over HTTP.

## Goals / Non-Goals

**Goals:**
- Import the Dmail tool extension **with history** into `tools/Dmon.Tools.Dmail/`, and its one self-contained test into `test/Dmon.Tools.Dmail.Tests/`.
- Port the extension to the current SDK: `Dmon.Abstractions.Extensions` + `IToolExtension`; rename to the ADR-023 D3 tool family `Dmon.Tools.Dmail`.
- Re-wire to monorepo conventions: `Dmon.Abstractions` `ProjectReference` (ADR-025 D4), central package management, MinVer + skew-guard from root props, packable like `Dmon.Tools.Builtin`, in `tools.slnx` + `Everything.slnx`.
- All gates green: `Everything.slnx` build, `make build`/`make test`, `dotnet pack` of the tool, `openspec validate --strict`, and `git log --follow` proving preserved history.

**Non-Goals:**
- The Dmail **server** graft — stays in `dmail`.
- Importing `dmail`'s own `openspec/` (its 7 server-side capabilities describe the server; the extension capability is re-authored here as a spec delta).
- Dependency-aware path-filtered CI, the two-family release matrix, ADR-024 per-package tag prefixes — later, per Phase 0.
- Any behaviour change to the extension or to existing dmon-core code.

## Decisions

**Import tool: `git filter-repo` on a throwaway clone (mirrors Phase 1).** `git-filter-repo` is acquired ad hoc with `uvx git-filter-repo` (fallback `brew install git-filter-repo`); it refuses to rewrite a repo with its origin/working changes, so operate on a fresh clone of the **`feat/dmon-tool-dmail`** branch, never the original repo:
```
git clone /Users/rendle/github/daemonicai/dmail /tmp/graft-dmail
cd /tmp/graft-dmail
git checkout feat/dmon-tool-dmail            # extension lives here, @ b1af562
uvx git-filter-repo \
  --path src/Dmon.Extensions.Dmail/ \
  --path test/Dmail.Tests/DmailExtensionTests.cs \
  --path-rename src/Dmon.Extensions.Dmail/:tools/Dmon.Tools.Dmail/ \
  --path-rename test/Dmail.Tests/DmailExtensionTests.cs:test/Dmon.Tools.Dmail.Tests/DmailExtensionTests.cs
```
`--path` keeps **only** the extension subtree (incl. its `README.md`) and the one self-contained test file — dropping the server, the server-coupled tests, the satellite's `openspec/`, `nuget.config`, vendored nupkgs, root README/LICENSE/CI, and `Directory.*` plumbing. The two `--path-rename`s relocate them to the bucket layout **with history**. The directory and the test file are renamed by filter-repo; `.csproj` *filename*, file contents, and namespaces are still the old `Dmon.Extensions.Dmail` / `Daemonic.Dmail.*` — fixed after the merge.

`DmailExtensionTests.cs` is verified self-contained: its four tests construct `DmailExtension` against an unreachable port (`http://localhost:9`) and assert tool names, descriptions, the permission policy, and unreachable-degradation. It references only the extension + `Dmon.Protocol.Enums` + `Microsoft.Extensions.AI` — **no** dependency on `src/Dmail`. The satellite's `test/Dmail.Tests/Dmail.Tests.csproj` is **not** imported (it `ProjectReference`s `src/Dmail`); a fresh test `.csproj` is authored post-merge.

**Merge: one-time `--allow-unrelated-histories`.** On the `change/graft-dmail` branch:
```
git remote add dmail-graft /tmp/graft-dmail
git fetch dmail-graft
git merge --allow-unrelated-histories dmail-graft/feat/dmon-tool-dmail -m "Import dmail extension history into tools/ (graft)"
git remote remove dmail-graft
```
No conflicts expected (imported paths are new to `dmon-core`). The throwaway clone is deleted afterward; the original `dmail` repo is left intact until the graft merges and verifies.

**Rename to the tool family (mirrors Phase 0 Omlx / Phase 1 LlamaCpp).** After the merge: `git mv` the extension `.csproj` to `Dmon.Tools.Dmail.csproj`; set `AssemblyName`/`RootNamespace`/`PackageId` = `Dmon.Tools.Dmail`. Rewrite the C# namespace `Daemonic.Dmail.Extension` → `Dmon.Tools.Dmail` across the src files (`DmailExtension`, `DmailClient`, `DmailModels`, `DmailApiException`) and `Daemonic.Dmail.Tests` → `Dmon.Tools.Dmail.Tests` in the test. A repo-wide grep (excluding `bin/obj`) for `Dmon.Extensions.Dmail`, `Daemonic.Dmail`, and `IDmonExtension` must return nothing.

**API port (ADR-022) — the bit Phase 1 did not need.** In `DmailExtension.cs`: `using Dmon.Extensions;` → `using Dmon.Abstractions.Extensions;`, and `: IDmonExtension` → `: IToolExtension`. The interface surface is identical (`Name`, `Description`, `IEnumerable<AIFunction> Tools`, `Evaluate(FunctionCallContent, IPermissionSettings, IPermissionSettings?)`), so the method bodies and the `DmonAIFunctionFactory.Create(...)` calls are unchanged (`DmonAIFunctionFactory` still ships in `Dmon.Abstractions`). Update the class doc-comment and the package `README.md` registration guidance from `builder.AddExtension<DmailExtension>()` to the current `builder.AddToolExtension<DmailExtension>()` verb.

**Intra-repo `ProjectReference` (ADR-025 D4).** Tool `.csproj`: replace `<PackageReference Include="Dmon.Extensions" Version="0.2.*" />` with `<ProjectReference Include="..\..\core\Dmon.Abstractions\Dmon.Abstractions.csproj" />`. Author a fresh `test/Dmon.Tools.Dmail.Tests/Dmon.Tools.Dmail.Tests.csproj` (`IsPackable=false`, `RootNamespace=Dmon.Tools.Dmail.Tests`) that `ProjectReference`s only `..\..\tools\Dmon.Tools.Dmail\Dmon.Tools.Dmail.csproj` — the dropped server reference is gone.

**Central Package Management.** Remove the tool's standalone `<Version>0.2.0</Version>` (MinVer drives the version) and drop inline `Version=` from all `PackageReference`s. The extension's only non-`Dmon` dependency is `Microsoft.Extensions.AI` (already pinned for the repo); the test's deps (xunit, coverlet, Test.Sdk, runner) are already centrally pinned from Phase 0 — reuse, don't duplicate. Add a `<PackageVersion>` to the root `Directory.Packages.props` only if a build surfaces a missing pin. The tool keeps `IsPackable=true`, `MinVerTagPrefix`, and a `Description`/`PackageTags` consistent with `Dmon.Tools.Builtin`; the `README.md` stays packed (`<None Include="README.md" Pack="true" .../>`).

**Solutions.** Add `tools/Dmon.Tools.Dmail/Dmon.Tools.Dmail.csproj` to `tools.slnx` (under `/tools/`) and the test under `/test/`; add both to `Everything.slnx`. Per-area `core.slnx` etc. are unaffected.

**OpenSpec capability provenance.** The `dmail-tool` capability is authored as this change's spec delta (`specs/dmail-tool/spec.md`, all `ADDED`) and syncs to `openspec/specs/dmail-tool/` on archive — the provenance pattern Phase 1 used for `llamacpp-provider`. The satellite's `openspec/` is deliberately not imported.

**Source-repo disposition.** Only the extension is grafted; the Dmail server remains a live, separate first-party repo. Record `dmail` as **absorbed-but-live** (a note in this change's DEVLOG + memory; optionally a local `extension-absorbed-into-dmon-core` git tag on `feat/dmon-tool-dmail`). Do not delete it.

**Versioning.** No ADR-024 tooling here. The tool inherits the monorepo's MinVer + protocol skew-guard via the root `Directory.Build.props`; `Major.Minor` must match `core/Dmon.Protocol/ProtocolVersion.cs` — verified by packing the tool.

## Risks / Trade-offs

- **`git filter-repo` unavailable / refuses to run** → use `uvx git-filter-repo` on a fresh clone (its safety checks pass on a clean clone); fallback `brew install git-filter-repo`. Gate the merge on a successful filtered clone.
- **API port misses a contract gap** (the surface is asserted identical, but `IToolExtension` may have a default `Evaluate` or extra member) → after the port, build `Dmon.Tools.Dmail` alone first; any signature mismatch surfaces immediately. The four ported tests assert the observable contract (tool set, descriptions, permission policy, degradation).
- **History not actually preserved** (wrong path-rename, or the rename wave not using `git mv`) → verify with `git log --follow tools/Dmon.Tools.Dmail/DmailExtension.cs` showing pre-graft commits before ticking; the rename wave must be `git mv` so blame survives the second hop.
- **Test split leaves a dangling reference** (the fresh test `.csproj` accidentally referencing `src/Dmail`, or an imported server test) → only `DmailExtensionTests.cs` is `--path`-kept; the test `.csproj` is authored fresh referencing only the tool. Build the test project to confirm no server type is needed.
- **CPM pin clash for `Microsoft.Extensions.AI`** (satellite pinned its own version) → the inline `Version=` is stripped; the repo's central pin wins. Build the full solution to surface any `NU1605`/downgrade.
- **Stale `Dmon.Extensions.Dmail` / `Daemonic.Dmail` / `IDmonExtension` references** anywhere (csproj filename, namespaces, slnx, README) → final repo-wide grep gate (excluding `bin/obj`) returns nothing.
- **Large unrelated-history merge is awkward to review** → keep the import as its own commit, then the rename/port/reref/CPM/slnx integration as the following commit(s); each must leave `Everything.slnx` building green.

## Migration Plan

No runtime migration: the tool is additive and only active when composed into an agent's `Dmon.cs` (no production deployments — clean break is fine for the name change). Rollback is `git revert` of the integration commit(s) and, if needed, the import merge; the original `dmail` repo is untouched throughout.

## Open Questions

- None blocking. (Per-area `openspec/` roots for grafted single-package specs — ADR-025's hybrid-root end state — are not yet instantiated; until then, the tool capability lives at root `openspec/specs/` consistent with `llamacpp-provider`. Revisit when the hybrid roots are stood up.)
