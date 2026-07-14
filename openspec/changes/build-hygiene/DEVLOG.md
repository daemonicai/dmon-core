# DEVLOG — build-hygiene

Architect split into 2 impl blocks + final gates:
- **Block A = Group 1 (1.1–1.4)** — centralize TWE/Nullable. **DONE.** (Carries all the surfaced-warning risk + edit volume.)
- **Block B = Groups 2+3 (2.1–2.2, 3.1–3.2)** — NU1903 scoping + Markdig pin. NEXT.
- Gates 4.1–4.3 ticked with Block B (final).

## NEXT
- **Change COMPLETE — all tasks ticked (1.x, 2.x, 3.x, 4.x).** Ready for PR + merge, then archive.

## Block B — NU1903 scoping (#9c) + Markdig pin (#9d) — DONE (tasks 2.1–2.2, 3.1–3.2)

Removed the repo-wide `NoWarn;NU1903` from root `Directory.Build.props` and scoped it to the **transitive closure** of SQLite consumers; pinned Markdig `1.*`→`1.3.2` and dropped the now-dead `CentralPackageFloatingVersionsEnabled`.

- **Suppression set = 18, NOT the design's estimated 6.** NU1903 is a NuGet restore-audit warning (audit mode `all`, no `PrivateAssets`) → propagates across `ProjectReference`. Enumerated authoritatively (remove repo-wide suppression → `dotnet build Everything.slnx -c Release -p:TreatWarningsAsErrors=false` → grep NU1903): **13 `.csproj`** (6 direct + `Daemon.Routing`, `Dmon.Network`, `Dmon.Core.Tests`, `Dmail.Tests`, `Daemon.Routing.Tests`, `Dmon.Network.Tests`, `Dmon.Tools.Dcal.Tests` — transitive) + **5 file-based composition roots** via `#:property NoWarn=$(NoWarn);NU1903` (`default-core/Dmon.cs`, root `Dmon.cs`, `samples/Dmon.{ComposedCore,MtplxCore,WebSearchCore}/Dmon.cs`). `Dmon.Core`/`Dmon.Network` appended NU1903 to their existing `NU1901` line. Markdig `1.3.2` == what `1.*` resolved to (no-op re-pin); Markdig was the only floater so the CPM opt-in dropped cleanly. Only NU1903/SQLitePCLRaw surfaced — no masked advisory unmasked (no stop-and-ask).
- **Review (2 rounds):** round 1 Request-changes — **B1**: the 4 non-default file-based roots (`Dmon.cs` + 3 samples) `#:package dmoncore`→SQLite and inherit root TWE but are compiled by NO gate (`make build`/`make test`/CI), so removing the repo-wide suppression silently regressed `dotnet run Dmon.cs` to a build failure. Fixed by adding the same `#:property` directive to all 4 (verified load-bearing: stripping it surfaces NU1903-as-error). **B2**: the 6→18 count exceeded design/tasks text — orchestrator reconciled tasks.md 2.2 + design.md D3 + this DEVLOG (correct transitive expansion, not scope creep). Round 2 **Approve** — closure complete + minimal (`Dmon.Terminal` correctly excluded: separate process, no SQLite ProjectRef), NU1008 confirmed pre-existing/out-of-scope.
- **HAZARD recorded for future authors:** any new file-based composition root (`.dmon/agents/*.cs`, new sample) that `#:package dmoncore` needs its own inline `#:property NoWarn=$(NoWarn);NU1903` (no shared props can carry it). Delete all 18 once `Microsoft.Data.Sqlite` ships SQLitePCLRaw ≥ 2.1.12.
- **Gate-4.1 fix:** amended tasks.md 4.1 to name `make smoke` as ExtensionSmoke's actual gate (reviewer nit from Block A).

**Gates:** `make build` 0-warn; full `make test` 20/20 projects green; `openspec validate --strict` valid. **Change complete.** (2) Remove `NoWarn;NU1903` from root `Directory.Build.props` and add `<NoWarn>$(NoWarn);NU1903</NoWarn>` to ONLY the 6 projects that transitively pull SQLitePCLRaw/`Microsoft.Data.Sqlite` (`Dmon.Core`, `Dmon.Memory`, `Dcal`, `Dmail`, `Dmon.Memory.Tests`, `Dcal.Tests` — RE-ENUMERATE, don't trust the list; grep for `Microsoft.Data.Sqlite`/`SQLitePCLRaw` package refs + transitive). (3) `Directory.Packages.props:48` Markdig `1.*` → the resolving `1.3.2`; drop the now-unneeded `CentralPackageFloatingVersionsEnabled` opt-in IF nothing else floats. Gate: after removing the repo-wide NU1903, `make build` must stay 0-warn (if a NON-sqlite project newly emits NU1903, that means it DOES pull sqlite → add it to the scoped set; if a project emits a DIFFERENT advisory that was masked, STOP-AND-ASK — do not re-suppress).
- **Gate-4 fix (orchestrator, at final tick):** amend tasks.md gate 4.1 to name `make smoke` as ExtensionSmoke's actual gate (reviewer nit — `make build` does NOT compile the out-of-tree sample; only `make smoke` does).

## Block A — centralize TWE/Nullable (#9a/#9b) — DONE (tasks 1.1–1.4)

Hoisted `TreatWarningsAsErrors=true` + `Nullable=enable` into the **single root `Directory.Build.props`** (no nested props exist, so it reaches every project incl. the file-based `default-core/Dmon.cs`) and deleted the per-project duplicates from **46 `.csproj`** (47 files total).

- **Enumeration (no stop-and-ask):** TWE 45 projects all `true`; Nullable 46 all exactly `enable` — no `disable`/`false`/`annotations` deviation to clobber. ExtensionSmoke had Nullable but no TWE → newly gains only TWE; `default-core` gains both.
- **No source fixes needed** — both newly-TWE targets compiled warning-clean.
- **Gate coverage subtlety:** `make build`/`make test` do NOT compile `samples/Dmon.ExtensionSmoke` (out-of-tree, only in `make smoke`). `default-core/Dmon.cs` IS covered by `make build`→`build-core`. So **1.4 is closed by `make smoke` PASS**, not make build.
- Untouched: root `NoWarn;NU1903` (Block B), `Directory.Packages.props` (Block B), all other per-csproj props (`ImplicitUsings`, `Dmon.Core`'s `NoWarn;NU1901`, `AssemblyName`/`PackageId`, `MinVerSkip`, `DmonSdkVersion`).

**Reviewer:** Approve, no blockers. Verified NO silent behaviour flip (every removed line verbatim `true`/`enable`; whole-tree grep for `disable`/`false` = none), counts match (45/46), nothing-else-removed, single root props (no shadow). Nit → the gate-4.1 `make smoke` fix above.

**Gates (orchestrator-run):** `make build` 0-warn; `make smoke` PASS (ExtensionSmoke 0-warn under TWE); full `make test` 20/20 projects green; `openspec validate --strict` valid.
