# The termination path is load-bearing on process-scoped resources only

**Status:** open — a standing constraint, not a bug
**Where:** `home/App/DmonHomeApp/AppDelegate.swift` — `applicationShouldTerminate` and `replyToTerminate`
**Surfaced:** 2026-08-06, by the block reviewer and endorsed by the section-5 supervisor
**Severity:** low now, high the moment it is violated — and it will be violated silently

## What

`applicationShouldTerminate` runs two independent racing tasks: the real
shutdown, and a budget timer that replies anyway if shutdown overruns. Neither
cancels the other. The activity-assertion release (task 5.2) sits **inside the
shutdown task**, so if the budget task wins, the release never runs.

That is sound — but only because a `ProcessInfo` activity assertion is
**process-scoped**, and a dead process cannot keep asserting. The OS reclaims it.

## Why it matters

The soundness argument is about the *kind* of resource, not about the code. Put
a **non**-process-scoped resource on that same path — a file lock, a remote
lease, a Tailscale registration, a gateway session the server tracks — and the
budget task winning means that resource is never released, with **no test
signal**, because the app target has no test bundle and the failure only appears
under a shutdown that overruns its budget.

Sections 6 and 7 (gateway client, session lifecycle) are the first plausible
place for exactly such a resource.

## What to do

Nothing now. Before adding **any** cleanup to the termination path, ask whether
the resource survives process death. If it does, it cannot rely on the current
structure — it needs either its own release ahead of the race, or a redesign in
which the budget cancels the shutdown rather than racing it.

## Honesty limit

The reviewer confirmed the header documents deallocation-triggers-release, but
**did not find process-exit release stated verbatim** in `NSProcessInfo.h`. That
last step is platform convention rather than a documented guarantee. It is
almost certainly true; it is not written down anywhere Apple committed to.
