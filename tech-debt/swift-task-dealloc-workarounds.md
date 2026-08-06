# Swift 6.3.3 async task-context crash, worked around twice

**Status:** open — workarounds in place, root cause not confirmed, not filed upstream
**Where:** `home/Sources/Supervisor/HostSupervisor.swift` (two sites), written up in `home/TOOLCHAIN-NOTES.md`
**Surfaced:** 2026-08-05/06, during blocks 5.1 and the section-5 remediation
**Severity:** medium — costs nothing today, but each recurrence costs a round of confused debugging

## What

Two unrelated declaration changes in `HostSupervisor` each reproducibly crash
the test suite with `malloc: freed pointer was not the last allocation`:

1. a `var [Task<Void, Never>]` field on `ChildState`, **even left entirely
   unpopulated** — worked around with two named optional fields;
2. a stored `static let defaultLogDrainGrace: TimeInterval = 2` — worked around
   with a computed `static var`.

The message names `malloc` and is misleading: the fault is in
`swift_task_dealloc`, Swift Concurrency's own task-context allocator. Both
triggers produce an **identical backtrace**, which is the evidence that they are
one defect and not two.

Full signature, both triggers, three audited-and-excluded in-house causes, and
the reproduction recipe are in **`home/TOOLCHAIN-NOTES.md`** — deliberately not
an ADR, because ADRs record decisions this project made and nothing here was
chosen.

## Why it matters

Neither workaround bends the design — two named optionals is arguably clearer
code, and a computed property for one `TimeInterval` costs nothing. That is why
this was judged acceptable to ship on. It stops being acceptable if a third
occurrence forces an *unnatural* shape, which is when a toolchain workaround
starts charging real architectural rent.

The defect's defining feature is that it strikes **unrelated** declarations, so
the next person to hit it will not be reading either workaround comment. That is
why the write-up is a file rather than a comment.

## What to do

1. **Needs the Product Owner: file it upstream.** The evidence is close to a
   filable Swift issue — runtime function, fatal-error path, source statement,
   caller, toolchain version, two independent triggers, three excluded causes.
   Filing is a public post, so it has not been done unattended. Not filing means
   occurrence three starts from zero.
2. **On any toolchain upgrade**, re-run the reproduction in `TOOLCHAIN-NOTES.md`
   (one command, deterministic in both directions). If it no longer reproduces,
   retire both workarounds and delete the note together.

## Honesty limits, carried from the investigation

- **Suspected, not confirmed.** No compiler-side root cause was established;
  what exists is an exclusion argument.
- **The upstream tracker was never searched** — "we did not look", not "we
  looked and found nothing".
- **The Address Sanitizer negative result rests on a single run**, against every
  other claim being established over 3–5.
