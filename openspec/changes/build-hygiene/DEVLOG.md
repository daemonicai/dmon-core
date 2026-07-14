# DEVLOG — build-hygiene

Architect split into 2 impl blocks + final gates:
- **Block A = Group 1 (1.1–1.4)** — centralize TWE/Nullable. **DONE.** (Carries all the surfaced-warning risk + edit volume.)
- **Block B = Groups 2+3 (2.1–2.2, 3.1–3.2)** — NU1903 scoping + Markdig pin. NEXT.
- Gates 4.1–4.3 ticked with Block B (final).

## NEXT
- **Block B = 2.x (NU1903 scoping) + 3.x (Markdig pin).** (2) Remove `NoWarn;NU1903` from root `Directory.Build.props` and add `<NoWarn>$(NoWarn);NU1903</NoWarn>` to ONLY the 6 projects that transitively pull SQLitePCLRaw/`Microsoft.Data.Sqlite` (`Dmon.Core`, `Dmon.Memory`, `Dcal`, `Dmail`, `Dmon.Memory.Tests`, `Dcal.Tests` — RE-ENUMERATE, don't trust the list; grep for `Microsoft.Data.Sqlite`/`SQLitePCLRaw` package refs + transitive). (3) `Directory.Packages.props:48` Markdig `1.*` → the resolving `1.3.2`; drop the now-unneeded `CentralPackageFloatingVersionsEnabled` opt-in IF nothing else floats. Gate: after removing the repo-wide NU1903, `make build` must stay 0-warn (if a NON-sqlite project newly emits NU1903, that means it DOES pull sqlite → add it to the scoped set; if a project emits a DIFFERENT advisory that was masked, STOP-AND-ASK — do not re-suppress).
- **Gate-4 fix (orchestrator, at final tick):** amend tasks.md gate 4.1 to name `make smoke` as ExtensionSmoke's actual gate (reviewer nit — `make build` does NOT compile the out-of-tree sample; only `make smoke` does).

## Block A — centralize TWE/Nullable (#9a/#9b) — DONE (tasks 1.1–1.4)

Hoisted `TreatWarningsAsErrors=true` + `Nullable=enable` into the **single root `Directory.Build.props`** (no nested props exist, so it reaches every project incl. the file-based `default-core/Dmon.cs`) and deleted the per-project duplicates from **46 `.csproj`** (47 files total).

- **Enumeration (no stop-and-ask):** TWE 45 projects all `true`; Nullable 46 all exactly `enable` — no `disable`/`false`/`annotations` deviation to clobber. ExtensionSmoke had Nullable but no TWE → newly gains only TWE; `default-core` gains both.
- **No source fixes needed** — both newly-TWE targets compiled warning-clean.
- **Gate coverage subtlety:** `make build`/`make test` do NOT compile `samples/Dmon.ExtensionSmoke` (out-of-tree, only in `make smoke`). `default-core/Dmon.cs` IS covered by `make build`→`build-core`. So **1.4 is closed by `make smoke` PASS**, not make build.
- Untouched: root `NoWarn;NU1903` (Block B), `Directory.Packages.props` (Block B), all other per-csproj props (`ImplicitUsings`, `Dmon.Core`'s `NoWarn;NU1901`, `AssemblyName`/`PackageId`, `MinVerSkip`, `DmonSdkVersion`).

**Reviewer:** Approve, no blockers. Verified NO silent behaviour flip (every removed line verbatim `true`/`enable`; whole-tree grep for `disable`/`false` = none), counts match (45/46), nothing-else-removed, single root props (no shadow). Nit → the gate-4.1 `make smoke` fix above.

**Gates (orchestrator-run):** `make build` 0-warn; `make smoke` PASS (ExtensionSmoke 0-warn under TWE); full `make test` 20/20 projects green; `openspec validate --strict` valid.
