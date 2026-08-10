# `ChildSpawnerTests` readiness rests on a fixed sleep

**Status:** open — has not been observed to flake
**Where:** `home/Tests/SupervisorTests/ChildSpawnerTests.swift` — `aSpawnedChildDoesNotInheritThisProcessesSignalMaskOrDispositions`
**Surfaced:** 2026-08-06, while fixing a different fixture's readiness race
**Severity:** low

## What

The test waits a fixed 200 ms `Task.sleep` before signalling the child, rather
than waiting on an event-based readiness marker the child itself writes.

## Why it is worth a note but not a fix

This is a **timing assumption**, not the early-signal defect that was just fixed
in `gracefulShutdownTriggersNoRestart`. That one had a real logical inversion —
the readiness marker was written *before* the `TERM` trap it was meant to signal
the existence of, so the signal was affirmatively wrong. Here the signal is
merely *estimated*.

The distinction matters because the rule that came out of that fix — **a
readiness signal must be written after the thing it signals readiness for** —
does not cover this case, and stretching it to cover this case would make it
vague. Better to keep the rule sharp and note this separately.

## What to do

Nothing unless it flakes. If it does, the fix is the same shape as the other
one: have the child write a marker once its signal state is actually
established, and wait on that marker instead of on a duration.

A loaded CI runner is the likely place for 200 ms to stop being enough — worth
remembering when the `home/` CI job lands (task 10.1), alongside the other
timing-headroom item already carried for `aHungCheckPublishesFailureWithoutBlockingOtherChildren`.
