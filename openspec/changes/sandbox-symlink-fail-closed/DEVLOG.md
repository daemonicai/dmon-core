# DEVLOG — sandbox-symlink-fail-closed

## NEXT

All tasks complete (1.1, 1.2, 2.1–2.4, 3.1–3.3). Reviewer signed off. Ready to
commit and (after user confirmation) archive.

---

## Block 1 — Fail closed on unresolvable symlinks (core) + tests + gates

**Tasks:** 1.1, 1.2, 2.1, 2.2, 2.3, 2.4, 3.1, 3.2, 3.3 (whole change, one block).

### What changed
- `core/Dmon.Core/Permissions/SandboxContainmentChecker.cs` — `FollowLinkToTarget`:
  - **1.1** Added `if (final is not null && !Path.Exists(final)) return null;` after the
    relative-path re-anchoring block, so a dangling symlink that resolves *without
    throwing* (Linux) fails closed identically to the platforms that throw
    (macOS/Windows). Verbatim the guard already shipped in the tools-layer twin
    `tools/Dmon.Tools.Builtin/RealPathResolver.cs` (commit `ac2e315`, PR #96).
  - **1.2** Corrected the backwards platform comment: macOS/Windows throw
    `IOException` for a dangling link; Linux returns the non-existent target without
    throwing; the explicit `Path.Exists(final)` guard fails both closed on every platform.
  - No changes to `ResolveRealPath`, `ResolveExistingAncestor`, `IsSymlink`, or `IsContained`.
- `test/Dmon.Core.Tests/Permissions/SandboxContainmentCheckerTests.cs` — Cases 11–13:
  - **2.1 / Case 11** `DanglingLeafSymlinkPointingInside_IsNotContained`.
  - **2.2 / Case 12** `DanglingAncestorSymlinkPointingInside_IsNotContained`.
  - **2.3 / Case 13** `LiveInSandboxSymlink_IsContained` (over-broad-guard regression);
    the "live symlink escaping" half is referenced to existing Case 5, not duplicated.
  - **2.4** CI-only-observable caveat comment added.

### Key decision / standing lesson (task 2.4)
**The fix is only observable on Linux/Ubuntu CI.** On macOS/Windows
`ResolveLinkTarget` throws for a dangling link, so the pre-existing `catch (IOException)`
already fails closed — the new tests pass locally *with or without* the guard. A green
local `make test` therefore does **not** prove the fix; **Ubuntu CI is the authoritative
verification** (the batch's standing symlink/filesystem lesson).

**Test-construction trap (architect-flagged, reviewer-confirmed):** the dangling
symlinks in Cases 11/12 must point at a non-existent path **inside** the asset dir.
Only then does the unguarded Linux path resolve to a non-null *in-root* target, making
`IsContained` return `true` (fail open) without the guard and `false` with it. Pointing
the link outside (as existing Cases 8/9/10 do) is rejected by the containment prefix
check on Linux regardless, so such a test gates nothing. This is why the new cases
differ in construction from Cases 8/9/10.

### Gates
- `make build` — 0 warnings / 0 errors (`TreatWarningsAsErrors`).
- `env -u MEKO_API_KEY make test` — green; `Dmon.Core.Tests` 613 passed / 1 skipped
  (10 existing + 3 new containment cases).
- `openspec validate sandbox-symlink-fail-closed --strict` — valid.

### Reviewer notes (non-blocking, out of scope)
- `catch (IOException)` does not catch `UnauthorizedAccessException` from a mid-chain
  permission error — **pre-existing**, identical in the twin, out of this change's scope.
  Future-hardening note only.
