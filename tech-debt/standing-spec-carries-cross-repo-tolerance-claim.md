# The `sessionStarted` tolerance claim rests on inspecting another repository

**Status:** open — **due at archive of `lazy-session-creation`; delete this note once done**
**Where:** `openspec/specs/agent-core/spec.md` (at sync), vs `openspec/changes/**/lazy-session-creation/{proposal.md,design.md}`
**Surfaced:** 2026-09-09, section-5 supervisor of `lazy-session-creation`
**Severity:** low, but it decays — the weaker claim becomes the standing record if nothing is done

## What

`lazy-session-creation` added the `sessionStarted` event and had to argue that
hosts which do not recognise it are undisturbed. Its `proposal.md` ("Impact →
Other hosts") and `design.md` (Risks) justify the `dmon-home` third of that
argument by **inspecting another repository**: *"dmon-home projects unrecognised
events to `nil`"*.

That claim cannot be tested from this repo and cannot be kept true from this
repo — `dmon-home` left (ADR-038) and now versions independently.

Section 3 of the same change replaced it with something stronger and in-tree:
`dmon-home` is a **gateway client**, and task `3.6` proves the gateway's two-step
`session.create` → path-less `session.load` handshake always leaves a session
active before any `turn.submit` is accepted. So the core **never emits
`sessionStarted` on a gateway-fronted session** — exposure is nil *by
construction*, not by faith in another repo's switch statement. `docs/protocol/README.md`
§5.3 already states this.

## Why it matters

`proposal.md` and `design.md` are historical artefacts and archive as written —
correctly, they record what was believed when the change was proposed. But the
**standing** `agent-core` spec is synced from the change's deltas at archive
time and then outlives it. If the cross-repo inspection is what gets carried
across, the durable record of *why* the event is safe will be an argument nobody
can check, about a codebase this repository does not contain.

## What to do

At archive, when syncing the `agent-core` delta into
`openspec/specs/agent-core/spec.md`, make sure the "Unknown-event tolerance is
preserved" material carries the by-construction argument:

> A gateway-fronted session is always active before `turn.submit` is accepted,
> so the core never emits `sessionStarted` on that path.

and not the "dmon-home projects unrecognised events to `nil`" formulation.

Note the one genuine condition, which is worth stating in the spec rather than
losing: this holds **only while the gateway continues to guarantee an active
session before accepting a turn**. Task `3.6`'s regression test pins that today.

Then delete this note.
