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

## NEXT

Block 1A committed. Next: the section-1 supervisor review over `d770b96..HEAD` (single-block section, still required), then open section 2.
