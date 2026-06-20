## Context

Phases 1–3 established and twice re-applied a history-preserving graft recipe (`git filter-repo` path-renames on a throwaway clone → one-time `--allow-unrelated-histories` merge → rename/CPM/slnx integration) for the `providers/`, `tools/`, and `memory/` buckets. Phase 2 (`graft-dmail`) imported only the Dmail **tool extension** (`tools/Dmon.Tools.Dmail`) and explicitly left the **server** in `../dmail` because no monorepo bucket fit a standalone service.

ADR-028 then created the `services/` bucket (standalone backing servers, app-versioned, off the protocol train) with `services/Dcal` as its first member and `services/README.md` as the documented pattern, and names **`services/Dmail` as the future home of the Dmail server**. This change is the fourth graft and the second `services/` member.

The source server is `../dmail` `src/Dmail/` on the **`main`** branch (current HEAD): an ASP.NET Core (`Microsoft.NET.Sdk.Web`) app — per-account IMAP IDLE ingestion, BERT ONNX embeddings, SqliteVec + FTS5 hybrid search (reciprocal-rank fusion), API-key + Google OAuth2 (PKCE) auth with Data-Protection-encrypted token storage, and a static `wwwroot/` admin dashboard. It has **no** `ProjectReference` to the dmon extension; the tool reaches it only over HTTP (`DMAIL_BASE_URL`/`DMAIL_API_KEY`). The repo also holds the runtime ONNX assets (`models/bge-micro-v2.onnx` + `vocab.txt`), a multi-stage `Dockerfile`, a `docker-compose.yml`, `.env.example`, and `.dockerignore` — all referenced relative to the `../dmail` repo root.

## Goals / Non-Goals

**Goals:**
- Import the Dmail server **with history** into `services/Dmail/`, its server-coupled tests into `test/Dmail.Tests/`, the ONNX `models/` into `services/Dmail/models/`, and the Docker/env artifacts alongside the server — `git log --follow` proving preserved history.
- Re-wire to `services/` conventions (the `services/Dcal` template): `Sdk="Microsoft.NET.Sdk.Web"`, `TreatWarningsAsErrors=true`, `Nullable`/`ImplicitUsings` enable, `AssemblyName`/`RootNamespace` = `Dmail`, `IsPackable=false`, `InternalsVisibleTo` the test assembly; central package management; in `services.slnx` + `Everything.slnx` with the test under `/test/`.
- Keep the server's runtime behaviour **byte-for-byte identical** — this is a move + project-rename, not a behaviour change.
- All gates green: `Everything.slnx` build (warnings-as-errors), `make build`/`make test`, `openspec validate --strict`, history-follow check.

**Non-Goals:**
- Any change to `tools/Dmon.Tools.Dmail` or any other existing dmon-core code/protocol — the tool already targets the server over HTTP and is untouched.
- Importing `../dmail`'s own `openspec/` (its 7 server capabilities); the server contract is re-authored here as a single `dmail-server` capability (mirrors the `dcal-sync` single-server-capability precedent and the Phase-2 re-author pattern).
- Editing the `monorepo-layout` standing spec's services enumeration — that is owned by the in-flight `daemon-app` change; touching it here would collide.
- ADR-024 per-package tag prefixes / two-family release-matrix CI wiring for the service — later, not in this change.
- Deleting `../dmail` (left intact on disk; recorded as fully absorbed).

## Decisions

**Graft source = `../dmail` `main`, `git filter-repo` on a throwaway clone.** `git-filter-repo` is acquired with `uvx git-filter-repo` (fallback `brew install git-filter-repo`); it refuses to rewrite a repo with origin/working changes, so operate on a fresh clone, never the original:
```
git clone /Users/rendle/github/daemonicai/dmail /tmp/graft-dmail-server
cd /tmp/graft-dmail-server
git checkout main
uvx git-filter-repo \
  --path src/Dmail/ \
  --path models/ \
  --path Dockerfile --path docker-compose.yml --path .env.example --path .dockerignore \
  --path test/Dmail.Tests/ \
  --invert-paths --path test/Dmail.Tests/DmailExtensionTests.cs   # NOTE: see two-pass below \
  --path-rename src/Dmail/:services/Dmail/ \
  --path-rename models/:services/Dmail/models/ \
  --path-rename Dockerfile:services/Dmail/Dockerfile \
  --path-rename docker-compose.yml:services/Dmail/docker-compose.yml \
  --path-rename .env.example:services/Dmail/.env.example \
  --path-rename .dockerignore:services/Dmail/.dockerignore
```
`--path` keeps only the server subtree, its ONNX models, the deployment artifacts, and the server tests. The **already-grafted** `test/Dmail.Tests/DmailExtensionTests.cs` must be dropped — `filter-repo` cannot mix `--path` (keep-list) and `--invert-paths` (drop-list) in one run, so this is **two passes**: pass 1 keeps the subtrees above (without the invert line); pass 2 runs `git filter-repo --invert-paths --path test/Dmail.Tests/DmailExtensionTests.cs` to delete that one file from history. Verify with `git ls-files` after each pass. The two `--path-rename`s relocate to the bucket layout **with history**; the `.csproj` filename, file contents, and `Daemonic.Dmail.*` namespaces are still the old names — fixed after the merge.

**The ONNX model is Git-LFS-tracked in dmon-core.** The 69 MB `bge-micro-v2.onnx` is stored as a Git-LFS object (root `.gitattributes`: `*.onnx filter=lfs diff=lfs merge=lfs -text`) rather than a regular blob, to avoid permanently bloating every clone of the repo. It was LFS-tracked in the source too. Mechanics: after the graft+integration, run `git lfs install --local` then `git lfs migrate import --include="*.onnx" --include-ref=refs/heads/change/graft-dmail-server --exclude-ref=refs/heads/main` — the `--exclude-ref=main` is **mandatory** so the rewrite is scoped to branch-unique commits and shared ancestry with `main` is preserved (an unscoped migrate rewrites all history and breaks the merge-base). `vocab.txt` (228 KB) stays a normal blob.

**Models live service-local at `services/Dmail/models/`.** The source server references `..\..\models\` (the `../dmail` repo root) and resolves them at runtime via `Path.Combine(AppContext.BaseDirectory, "models", ...)`. Putting them under `services/Dmail/models/` keeps the service self-contained (multiple services may ship their own models) and the runtime path (`AppContext.BaseDirectory/models`) unchanged. After the merge, rewrite the csproj `Content Include="..\..\models\..."` to `models\...` (relative to the project) and the Dockerfile `COPY models/...` to `COPY services/Dmail/models/...`.

**Docker/compose paths re-rooted to the monorepo.** The `Dockerfile` `COPY src/Dmail/... / src/ / models/...` lines and the `docker-compose.yml` `build.context`/`dockerfile` are repo-root-relative in `../dmail`. After the merge, re-root them to the monorepo: build context stays the repo root, but paths become `services/Dmail/...`. Keep the artifacts under `services/Dmail/` (service-local, like the source) rather than merging into the repo-root `docker-compose.yml` (which only defines the aspire-dashboard and is unrelated). Document the build invocation in the service `README.md` (or DEVLOG) since the context is the repo root, not `services/Dmail/`.

**Merge: one-time `--allow-unrelated-histories`.** On the `change/graft-dmail-server` branch:
```
git remote add dmail-server-graft /tmp/graft-dmail-server
git fetch dmail-server-graft
git merge --allow-unrelated-histories dmail-server-graft/main -m "Import dmail server history into services/ (graft)"
git remote remove dmail-server-graft
```
No conflicts expected (imported paths are new to `dmon-core`). Delete the throwaway clone afterward; leave `../dmail` intact.

**Rename to the `services/` convention (mirrors `services/Dcal`).** After the merge: `git mv` `services/Dmail/Dmail.csproj` (keep filename `Dmail.csproj`); set `AssemblyName`/`RootNamespace` = `Dmail`, `IsPackable=false`, add `TreatWarningsAsErrors=true` and the `InternalsVisibleTo` `Dmail.Tests` attribute (per the Dcal template). Rewrite the C# namespace `Daemonic.Dmail` → `Dmail` (and `Daemonic.Dmail.Data/.Services/.Models` → `Dmail.Data/.Services/.Models`) across all server files, and `Daemonic.Dmail.Tests` → `Dmail.Tests` across the test files. A repo-wide grep (excluding `bin/obj`) for `Daemonic.Dmail` must return nothing.

**Test project: re-point, drop the extension reference.** The source `test/Dmail.Tests/Dmail.Tests.csproj` `ProjectReference`s `src/Dmail` and (transitively, via `DmailExtensionTests.cs`) the extension. After dropping `DmailExtensionTests.cs`, re-point the single `ProjectReference` to `..\..\services\Dmail\Dmail.csproj`, set `RootNamespace=Dmail.Tests`, `IsPackable=false`, and strip any inline `Version=`/extension reference. The remaining 10 test files (`ApiKeyServiceTests`, `TokenProtectionServiceTests`, `ReciprocalRankFusionTests`, `EmailTextExtractionTests`, FTS/vector integration tests, `AccountIndexStatusTests`, `VectorStore*`) reference only server types.

**Central Package Management.** Strip inline `Version=` from the server's `PackageReference`s; add the missing pins to the root `Directory.Packages.props`: `MailKit` (4.*), `MimeKit` (MailKit's companion, if referenced directly), `Microsoft.Extensions.VectorData.Abstractions`, `Microsoft.SemanticKernel.Connectors.Onnx`, `Microsoft.SemanticKernel.Connectors.SqliteVec`, and the DataProtection package if not transitive. `Microsoft.Data.Sqlite` is already pinned (10.0.8) — reuse. The test's deps (xunit, coverlet, Test.Sdk, runner) are already centrally pinned — reuse. Resolve the actual SemanticKernel/VectorData versions from a clean restore (the source pinned `1.74.*-*` prereleases and `10.1.0`); pick concrete pins that restore cleanly under the repo's `nuget.config`.

**Solutions.** Add `services/Dmail/Dmail.csproj` to `services.slnx` (under `/services/`) and `test/Dmail.Tests/Dmail.Tests.csproj` under `/test/`; add both to `Everything.slnx`. Other per-area `.slnx` are unaffected.

**OpenSpec capability provenance.** `dmail-server` is authored as this change's spec delta (`specs/dmail-server/spec.md`, all `ADDED`) and syncs to `openspec/specs/dmail-server/` on archive — the provenance pattern Phases 1–3 used. `../dmail`'s 7 server specs are not imported.

**Source-repo disposition.** `../dmail` is now **fully absorbed** (both extension and server grafted). Record it in DEVLOG + memory; optionally a local `absorbed-into-dmon-core` git tag on `../dmail` `main`. Do not delete it.

## Risks / Trade-offs

- **`TreatWarningsAsErrors` is repo-wide; the source server was not built warnings-as-errors** → the graft may surface warnings (e.g. `#pragma warning disable SKEXP0070` is already present for the experimental SK API; nullable/async warnings may appear). Fix them as part of integration without suppressing analyzers; if a warning is from an experimental-API attribute, scope a narrow `#pragma`/`<NoWarn>` only as the source already does. Build `services/Dmail` alone first to surface them early.
- **`git filter-repo` two-pass keep-then-drop** (cannot combine `--path` and `--invert-paths`) → run as two sequential `filter-repo` invocations on the clone; verify `git ls-files` shows the server subtree present and `DmailExtensionTests.cs` absent before the merge.
- **SemanticKernel / VectorData prerelease pins may not restore** under the monorepo `nuget.config` (the source pinned floating `1.74.*-*`) → resolve concrete versions from a clean restore; add exact `<PackageVersion>` pins; a floating prerelease must not leak into CPM.
- **ONNX model path/Docker path drift** → after re-rooting `..\..\models\` → `models\` and the Dockerfile `COPY` lines, confirm `make build` copies the model next to the binary (the csproj `Content Include` with `CopyToOutputDirectory`) and that a `docker build` from the repo root finds `services/Dmail/...`. The model files are ~large binaries committed via the graft — confirm they import (not LFS-stripped).
- **History not actually preserved** (wrong rename, or the rename wave not using `git mv`) → verify `git log --follow services/Dmail/Program.cs` shows pre-graft commits before ticking; the rename wave must be `git mv` so blame survives.
- **Integration-test reliance on network/IMAP** → the server tests are unit/integration over SQLite/FTS/vectors and token protection (no live IMAP); confirm `make test` runs them without network. Any test that would dial a live service must be skipped/categorised, not left to hang (cf. the MEKO live-smoke lesson — run workers with `env -u MEKO_API_KEY make test`).
- **Large unrelated-history merge is awkward to review** → keep the import as its own commit, then rename/reref/CPM/slnx/Docker-path integration as following commit(s); each must leave `Everything.slnx` building green.

## Open Questions

- None blocking. The `monorepo-layout` services enumeration update is deferred to the `daemon-app` change (avoids a cross-change edit collision); if `daemon-app` archives first, a trivial follow-up can add the `services/Dmail` row.
