# Silent failure when a child's executable cannot be resolved

**Status:** open
**Where:** `home/Sources/Supervisor/HostSupervisor.swift` — `apply`'s `.launchNotDecided` / `.executableNotResolved` / `.spawnFailed` branch
**Surfaced:** 2026-08-06, by the section-5 supervisor of `dmon-home-foundations`
**Severity:** high for a user, low for the contract — no requirement is violated, but the feature's stated purpose is defeated

## What

That branch appends nothing to the log store **and** publishes nothing to the
supervision store. So the single most likely real-world failure — `ndmon` is not
installed, so no launch candidate resolves — renders as:

| pane | shows |
|---|---|
| health | `unknown` |
| supervision | `normal` |
| log | `No output yet` |

Three panes, no signal, for a host that has definitively failed to start its
only enabled child.

## Why it matters

The requirement this section satisfied says the log pane exists *"so failures
are diagnosable without leaving the app"*. The mechanism that would fix it —
`ChildLogSource.host`, a channel for the host to say something about a child the
child could not say itself — **was built in section 5 and pointed at the one
branch that is not a failure** (`.adopted`).

Strictly the scenarios are met: a child that never started never wrote to
stdout, so "output is visible" is *unreachable* here rather than unmet. That is
why it was parked rather than blocked. It is still the highest-value thing on
this register.

## What to do

Emit a `.host`-sourced line on that branch naming the child and which candidates
were tried, and publish a supervision state that is not `.normal`. Roughly three
lines in a branch that already exists.

Note the constraint the section already established: **model facets earn the
"built for the full inventory" shelter; observation states do not.** If this
needs a new `ChildSupervisionState` case, that is a real widening and needs the
argument made, not assumed.

## Where it belongs

Task **8.3** (`dmon-home-foundations`) — "surface supervised-child health and the
gateway connection state in the UI … so a failed turn can be attributed to the
right layer". That is the first task that asks the UI to *explain* a failure
rather than name it, which is exactly this.

Both the Architect and the section-5 supervisor independently judged 8.3 the
right home rather than a fix inside section 5.
