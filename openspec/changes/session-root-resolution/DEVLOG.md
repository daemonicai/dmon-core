# DEVLOG — session-root-resolution

Working record for the change. Organised by `## N.` section, mirroring `tasks.md`.
Append-only; only `## NEXT` is rewritten.

Branch: `change/session-root-resolution`. The proposal was committed on
`fix/session-root-resolution` and renamed before apply; it was never pushed.

Provenance: `tech-debt/live-e2e-test-writes-into-home-session-store.md`. The Product
Owner chose option A on 2026-09-10 (`.dmon/config.yaml` is the root marker), added the
`Live` trait (design D7), and approved the proposal.

## 1. Pin the rule and correct the record

**[architect]** Base: `d770b96` — pins the `.dmon/config.yaml` root-marker rule with a paired resolver test, and adds the ADR-004 amendment note that resolves its self-contradiction.

**[architect]** Block 1A (`1.1`–`1.2`). The worker writes `1.1` (the resolver tests). I write `1.2` (the ADR-004 amendment note), since doc-only realignment is the Architect's. Both ship in one commit; the reviewer audits both.

Brief to worker (1.1):

- **Task 1.1 (verbatim):** In `test/Dmon.Core.Tests/Session/SessionDirectoryResolverTests.cs`, add the paired cases from design D4. (a) A temp tree whose `.dmon/` holds only `config.local.yaml`, with no `config.yaml` anywhere in its ancestors, resolves to the global `~/.dmon/sessions` path. (b) Adding `.dmon/config.yaml` to the same tree makes it resolve to `<tree>/.dmon/sessions`. Verify: both tests pass, and (a) fails if `FindDmonRoot` is changed to accept a bare `.dmon/` directory (check by temporarily making that change, then reverting it). The temp tree must sit under a path with no ancestor `.dmon/config.yaml` (not under the repo or `$HOME`).
- **Binding:** design D1 (root marker = `.dmon/config.yaml`, Product Owner's ruling) and D4 (paired negative and positive cases, beside the existing tests). The spec's ADDED scenario *"A .dmon/ directory without config.yaml is not a root"* is the one (a) pins.
- **Scope:** tests only. **No production code changes.** `SessionDirectoryResolver` is correct as it stands. The mutation check in the verify step is temporary and must be reverted, with the revert confirmed by `git diff core/` being empty.
- **Investigate first:** `core/Dmon.Core/Session/SessionDirectoryResolver.cs` (78 lines; `FindDmonRoot` checks `File.Exists(.dmon/config.yaml)`), and the existing `SessionDirectoryResolverTests.cs`, to match its temp-dir and `IConfiguration` setup and its naming.
- **Hazard:** `Path.GetTempPath()` on macOS is `/var/folders/…`, which is not under `$HOME`. Confirm that it has no ancestor `.dmon/config.yaml`, or the negative test proves nothing. Assert the exact global path (`Path.Combine(UserProfile, ".dmon", "sessions")`), not just "not local".
- **Gates (worker self-test):** `dotnet build` of `test/Dmon.Core.Tests` warning-free, and `env -u MEKO_API_KEY dotnet test test/Dmon.Core.Tests --filter "FullyQualifiedName~SessionDirectoryResolver"` green. Do **not** run `make test`: the Architect runs the full gates serially afterwards (concurrent builds race on `$TMPDIR`).

**[worker]** Block 1A (`1.1`) complete. Added `Resolve_DmonDirOnlyConfigLocal_ReturnsFallbackGlobalPath` (negative: a `config.local.yaml`-only `.dmon/` resolves to exactly `~/.dmon/sessions`) and `Resolve_DmonDirConfigLocalAndConfigYaml_ReturnsLocalSessions` (positive: the same tree plus `config.yaml` resolves to exactly `<tree>/.dmon/sessions`). Also added a precondition helper, `AssertNoAncestorConfigYaml`, which fails the negative test loudly if the temp tree ever sits under a real `.dmon/config.yaml`, so the test cannot pass vacuously. The existing `Resolve_DmonDirNoConfig_ReturnsLocalSessions` already covers `config.yaml` with no `sessionStore` key, so no duplicate was added. Mutation check: making `FindDmonRoot` also accept a bare `.dmon/` directory made the negative test fail (expected `~/.dmon/sessions`, got the `/var/folders/…` local path). Reverted; `git diff core/` is empty. 8/8 resolver tests pass.

**[architect]** `1.2`: the ADR-004 amendment note is a dated blockquote under the status line, in the ADR-012/017/018 style, marked "clarification only". It says the prose's "`.dmon/` directory" and step 5 mean a `.dmon/` containing `config.yaml`, as step 1 states. No other ADR-004 line changed.

**[reviewer]** Block 1A: **Approve**, with no blockers and no nits. Repeated the mutation independently: exactly one test failed, the negative case, and the other 7 passed. Reverted and confirmed `SessionDirectoryResolver.cs` is byte-identical to `HEAD`. Confirmed that the precondition guard walks the same ancestor chain as `FindDmonRoot`, that paths are asserted exactly, that per-test temp roots are parallel-safe, and that the ADR note's claim matches the code.

**[architect]** Block 1A gates: `make build` clean (0 warnings); `env -u MEKO_API_KEY make test` exit 0, every assembly green (Core 628 passed / 1 skipped, up 2); `openspec validate --strict` valid. `Dmon.Terminal.Tests` passed 194/194 in **19 s**. Earlier the same day it took 17 min in one run and hung in another (`tech-debt/terminal-tests-hang.md`), so that hang is intermittent, not constant. Ticked `1.1`–`1.2`.

**[supervisor]** Section 1 (`d770b96..688ae5b`): **Request changes.** The tests are sound and mutation-checked. **B1:** the ADR-004 note says *"the code has always implemented step 1"*, but step 1 is "walk up for `.dmon/config.yaml` → **read sessionStore**". The code does the walk-up, but reads `sessionStore` from the merged `IConfiguration`, which `DmonHostBuilder.cs:44-50` layers from the **working directory's** `.dmon/`, not the discovered root's. From `<root>/src`, a root's `sessionStore: global` is ignored. The spec delta's redirect scenario ("the project root's `.dmon/config.yaml` contains `sessionStore: global`") states that false behaviour as a SHALL. Also: the bootstrap scenario's THEN overstates (`sessions/` is not necessarily empty, and `created[]` always lists all three paths). The design's Risks bullet wrongly calls the `sessionStore: local` marker inert (it overrides the developer's global setting). Scenario coverage: bootstrap has no test anywhere, and the redirect is tested only with injected config. For section 2: the marker must carry `sessionStore: local` explicitly, and the core's working directory must be the temp root. For the register: `~/.dmon/config.yaml` makes `$HOME` a root, and `BootstrapService` is untested and re-implements the walk-up.

**[architect]** B1 verified against `DmonHostBuilder.cs:44-50`. It is a spec-versus-code conflict, so it went to the Product Owner, with three options: state only what is true and decided; match the code; or fix the code here. **The Product Owner chose option 1** (2026-09-10): state only what is true and decided, and park the subdirectory behaviour as explicitly undecided.

**[architect]** Remediation block 1R (doc-only, no task numbers, ticks nothing):
- **Spec delta:** the requirement now says a root's `.dmon/sessions/` is used when the *effective* `sessionStore` is `local`/unset. It states that a root's `config.yaml` contributes when invoked **from the root itself**, and says explicitly that the subdirectory case is **not specified**. The project-local scenario is conditioned on the effective `sessionStore`. The redirect scenario is limited to "invoked from the project root itself". The bootstrap THEN now says "ensures … exists / writes a default `config.yaml` / `created[]` names those three paths".
- **ADR-004 note:** now claims only the marker, and adds a "known gap, not resolved by this note" sentence pointing to the new register note.
- **design.md Risks:** corrected. The marker is not inert and must carry `sessionStore: local`, and the core must run with the temp root as its working directory. Section 2's briefs rely on this.
- **Register (3 new notes, 1 update):** `session-store-setting-ignored-from-subdirectory.md` (undecided), `home-dmon-config-makes-home-a-project-root.md`, `bootstrap-service-untested-and-duplicates-root-walk.md`; `terminal-tests-hang.md` updated (third run passed in 19 s, so the hang is intermittent).
- `openspec validate --strict`: valid.

**[reviewer]** Block 1R: **Approve.** Every claim was checked against `DmonHostBuilder.cs`, `SessionDirectoryResolver.cs` and `BootstrapService.cs`. Each SHALL and scenario is true for every working directory it covers, and the subdirectory case is left open, not asserted either way. Nits: a line reference was stale (`:75-104`, actually `:76-107`); and `sessionStore: <absolute path>` is now unspecified.

**[architect]** Both nits applied: fixed the line reference, and added a step 4 to `session-store-setting-ignored-from-subdirectory.md` to decide the path form together with the subdirectory case. Gates: the diff is `.md`-only (checked: no non-`.md` file changed), so `make build`/`make test` cannot be affected and were not re-run; `openspec validate --strict` is valid.

## NEXT

Block 1R committed. Next: re-run the section-1 supervisor over `d770b96..HEAD` (round 2 of 2). If it still requests changes, stop and go to the Product Owner.
