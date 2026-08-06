# Three near-identical stores — deliberately not collapsed

**Status:** open by decision — the trigger to revisit is recorded so it is not re-argued each time
**Where:** `home/Sources/Supervisor/ChildHealthStore.swift`, `ChildSupervisionStore.swift`, `ChildLogStore.swift`
**Surfaced:** 2026-08-06, put to the section-5 supervisor as an explicit question
**Severity:** low — this note exists to prevent repeated re-litigation, not to prompt work

## What

Three actors now share the same shape: a snapshot, a subscriber dictionary, an
`updates() -> AsyncStream`, and `onTermination` unregistration. `ChildLogStore`
was deliberately asked to copy the existing idiom rather than invent a second
one.

## The ruling: leave them

From the section-5 supervisor, and I agree:

- The duplicated part is **~20 lines of subscriber plumbing**. The payload
  semantics genuinely differ — replace-a-value versus
  append-with-capacity-and-drop-count.
- **Swift actors cannot inherit.** Collapsing means either a generic composed
  `SnapshotPublisher<Snapshot>` actor — which buys **an extra actor hop on every
  log line, on the hot path** — or `@unchecked Sendable` machinery that design
  D14 reserves for elsewhere.
- Three cheap parallel things beat one abstraction that costs latency on the
  busiest of the three.

## The trigger

**A fourth `snapshot + subscribers + updates() + onTermination` actor is the
point to extract.** Not a judgement call at the time — that is the rule, decided
in advance precisely so the fourth author does not have to re-run this argument
under deadline.

If the fourth one arrives, extract the plumbing and leave the payload semantics
in the concrete types.
