# A crashed child's descendants are never group-killed

**Status:** open
**Where:** `home/Sources/Supervisor/HostSupervisor.swift` — `handleExit`'s non-intentional branch
**Surfaced:** 2026-08-04, by the section-4 supervisor of `dmon-home-foundations`
**Severity:** medium — leaks processes for the host's lifetime, but only when a child spawns descendants and crashes

## What

`handleExit` clears `spawnedChild` on **every** exit including a crash, without
killing the process group. The subsequent restart spawns into a *new* group, and
`shutdownChild` then returns early because `spawnedChild` is `nil` — so anything
the crashed generation spawned survives for the rest of the host's life.

## Why it matters

This is design **D6**'s stated hazard — the orphaned model runtime — arriving by
the crash path rather than the exit path. It is **not** an unmet requirement:
requirement 5 says spawned children are killed by process group *"on exit"*, and
that is honoured. It is pre-existing rather than a regression; the process-group
sweep that section 4 deleted missed it identically.

It matters more as the child inventory grows. Today one child is enabled and it
is a .NET host that does not spawn helpers; the mlx runtimes and the speech
path, when they land, are exactly the descendants-spawning shape this protects
against.

## What to do

Cheap now that the code is shaped for it: `handleExit`'s non-intentional branch
has `child` in hand before it clears state, so the group kill can happen there.

Verify against the section-4 precedent for this class of test — the grandchild
fixture must put its `trap '' TERM` on the *grandchild*, not the leader, because
**ignored dispositions survive `exec` while caught ones do not**. An earlier
block got this wrong and produced a test that passed for the wrong reason.

## Provenance

Recorded by the section-4 supervisor and re-confirmed as still open when
section 5 closed. **Not re-verified against the code after section 5's
concurrency refactor**, which changed how `states[id]` is mutated but not the
kill behaviour — check before relying on the exact line references.
