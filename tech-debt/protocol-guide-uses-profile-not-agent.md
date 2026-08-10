# The protocol guide still says `profile` where the wire says `agent`

**Status:** open
**Where:** `docs/protocol/README.md` §3.2 (the `create` frame example and its rejection codes)
**Surfaced:** 2026-08-10, during `dmon-home-foundations` §10 task 10.5's close-code survey (pre-existing, out of that change's scope)
**Severity:** minor — but it is the *client-facing* guide, which raises the cost

## What

The guide documents session creation as:

```json
{"gw":"create","profile":"coding"}
```

with a rejection code `"unknown_profile"`. The wire has moved on: `CreateFrame` carries
`Agent`, and the rejection code is `"unknown_agent"` — ADR-022 superseded the ADR-013
profile bundle with agents (a `.cs` composition root under `.dmon/agents/`).

**Verified**, not inferred: the reviewer and worker both read the C# during 10.5 and the
mismatch is in the field name *and* the rejection code, not merely in prose around them.

## Why it matters more than a normal doc nit

`openspec/specs/protocol-schema/spec.md` designates `docs/protocol/` as the **client-facing
protocol guide** — what "an author building a client" reads. A client written from §3.2 as
it stands sends a field the host does not read and matches on a rejection code the host
never emits. It fails at the first `create`.

This is the same class of defect `dmon-home-foundations` §9 spent three supervisor rounds
removing from this very file — a statement that was true when written and silently stopped
being true. §9 corrected the ack and dedup passages; nobody swept §3.2, because nothing
pointed at it until 10.5's survey walked the create path.

## What to do

Rename to `agent` / `unknown_agent` in §3.2, sourcing both from
`core/Dmon.Protocol/Gateway/ControlFrames.cs` and `frontends/Dmon.Network`'s rejection
sites rather than from this note. Check the surrounding prose for "profile" as a *concept*
as well as a field name — ADR-022 retired the term, so a sentence about "selecting a
profile" is as stale as the JSON.

**Worth doing as part of a wider sweep, not alone:** nobody has audited the rest of
`docs/protocol/README.md` against the current wire. §9 and 10.5 each corrected the parts
they happened to walk past. A deliberate pass — read the guide against
`ControlFrames.cs` and `NetworkConnectionEndpoint.cs` end to end — is the thing that would
actually close this out, and it is unowned.

See also the existing [`docs-drift-pass.md`](docs-drift-pass.md) note, which tracks a
separate set of documentation drift items.
