# DEVLOG — graft-dmail-server

## Block 1.1–7.4 — full graft (one block)

The change is a history-preserving graft; there is no independently-green intermediate cut between "import" and "fully integrated" (the import alone won't build under CPM + warnings-as-errors), so the architect planned it as a single block, matching the three prior grafts.

### What landed
- **Import (commit `169a136`)** — `git filter-repo` two-pass on a throwaway clone of `../dmail` `main`: pass 1 kept `src/Dmail/`, `models/`, Docker/env artifacts, `test/Dmail.Tests/` with path-renames into `services/Dmail/` (+ `services/Dmail/models/`); pass 2 `--invert-paths` dropped `DmailExtensionTests.cs` (already grafted in Phase 2). Merged with `--allow-unrelated-histories`.
- **LFS handling (key decision — revised):** `*.onnx` was Git-LFS-tracked in the source. The graft first imported it as a normal blob (via `git lfs migrate export` in the throwaway clone before filter-repo). On review we decided dmon-core should **also LFS-track** the 69 MB model rather than carry it as a regular blob (avoids permanent repo-size bloat for every clone). So after the integration commit we ran **`git lfs install --local`** + **`git lfs migrate import --include="*.onnx" --include-ref=refs/heads/change/graft-dmail-server --exclude-ref=refs/heads/main`** — the `--exclude-ref=main` scopes the rewrite to the **13 branch-unique commits only**, preserving shared ancestry with `origin/main` (merge-base unchanged at `808c977`; `main` untouched). This added `*.onnx filter=lfs diff=lfs merge=lfs -text` to the root `.gitattributes` on the branch. The model is now an LFS object (`git lfs ls-files` shows it; working tree smudges to the real 69,035,106-byte file).
  - **Gotcha for the next reader:** a first attempt without `--exclude-ref=main` rewrote **all 584 commits** (adding the `.gitattributes` line everywhere), destroying common ancestry with `origin/main`. Always scope `git lfs migrate import` with `--exclude-ref=main` (or a `main..HEAD` range) on a feature branch. Recovered by resetting to the still-clean `origin/change/graft-dmail-server` and re-running scoped.
- **Rename** `Daemonic.Dmail*` → `Dmail*` across all sources + tests (single scoped `sed`); csproj `RootNamespace`/`AssemblyName` = `Dmail`. Repo-wide grep for `Daemonic.Dmail` is empty.
- **csproj** rewritten to the `services/Dcal` template: `Sdk=Microsoft.NET.Sdk.Web`, `TreatWarningsAsErrors=true`, `IsPackable=false`, `InternalsVisibleTo Dmail.Tests`; dropped `<OutputType>Exe</OutputType>` (Web SDK implies it). Model `Content Include` re-rooted `..\..\models\` → `models\`.
- **Test csproj** repointed to `..\..\services\Dmail\Dmail.csproj`, extension ProjectReference dropped, CPM (inline versions stripped). Test's direct usings (DataProtection, M.E.AI, VectorData, SqliteVec) resolve transitively via the server ProjectReference — no extra refs needed.
- **CPM pins** (concrete, resolved from the source `project.assets.json`; no floating prerelease): `MailKit 4.17.0`, `Microsoft.Extensions.VectorData.Abstractions 10.1.0`, `Microsoft.SemanticKernel.Connectors.Onnx 1.74.0-alpha`, `Microsoft.SemanticKernel.Connectors.SqliteVec 1.74.0-preview`. `MimeKit` is transitive-only → not pinned. `Microsoft.Data.Sqlite` reused (10.0.8).
- **Docker re-root** to a repo-root build context: Dockerfile copies the CPM trio (`Directory.Build.props`/`Directory.Packages.props`/`nuget.config`) before restore, COPY paths `services/Dmail/...`, `-p:MinVerSkip=true` (unversioned app image); compose `context: ../.. , dockerfile: services/Dmail/Dockerfile`. Added `services/Dmail/README.md`.
- **Solutions**: server + test added to `services.slnx` and `Everything.slnx`.

### Gates
- `services/Dmail` build: 0 warnings / 0 errors (existing `#pragma warning disable SKEXP0070` retained — the only suppression).
- `test/Dmail.Tests`: **54/54 pass**.
- `dotnet build Everything.slnx -c Release`: 0 / 0. `make build`: clean.
- `openspec validate graft-dmail-server --strict`: valid.
- Reviewer (Opus): **APPROVE**, no blockers.

### Pre-existing, out-of-scope test failure (NOT this block)
`env -u MEKO_API_KEY make test` shows 3 failures in `Dcal.Tests.CalendarSyncServiceTests` (off-by-one occurrence counts). **Proven to fail identically on clean `main`** — Dcal is untouched by this branch (`git diff main -- services/Dcal test/Dcal.Tests` empty). Root cause: tests hard-code `DTSTART:20260620…` and the service windows occurrences from "now", so today's date drops the first occurrence. Tracked and fixed separately by the **`dcal-sync-clock-seam`** change (TimeProvider seam + FakeTimeProvider). Task 7.2's "all existing tests green" is met modulo this independent pre-existing failure.

### Notes for the next reader
- History evidence: use `git log -- services/Dmail` (6 pre-graft commits). `git log --follow services/Dmail/Program.cs` under-reports (git's rename heuristic stops at the file's last content change) — not lost history (reviewer nit #1).
- `../dmail` is now **fully absorbed** (extension in Phase 2, server here). Left intact on disk.
