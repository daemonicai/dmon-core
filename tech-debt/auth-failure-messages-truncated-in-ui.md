# Auth failure messages are truncated in the UI, discarding the remedy

**Status:** open
**Where:** `home/App/DmonHomeApp/` — the gateway-status line that renders
`DeviceAuthDecision`'s message; messages authored in `home/Sources/DeviceKeys/DeviceAuthPolicy.swift`
**Surfaced:** 2026-09-07, running Phases 3 and 4 of `home/VERIFICATION-NOTES.md`
**Severity:** medium — the mechanism works, the user just cannot read the half that helps them

## What

`DeviceAuthPolicy` returns `.keyRevoked`, `.keyUnknownToStore` and `.secretMismatch`, each with
a carefully written message in two parts: **what happened**, then **what to do about it** — a
literal `security delete-generic-password …` command that lets the host re-provision.

The UI renders the message on a single line and clips it. Observed with `.secretMismatch`: the
user could read up to

> This device's stored key (keyId "DD81F949-…") no longer matches the credential the network
> host's device store records for it

and nothing after — so the parenthetical explaining *why* (rotation, or a backup restored from
either side of this one) and the entire remedy were invisible. The same clipping was visible
earlier in the run on the raw `NSURLErrorDomain -1011` connect-failure text, which ended in an
ellipsis mid-`UserInfo`.

## Why it matters

These five outcomes exist *specifically* so an auth failure names itself instead of surfacing
as a bare 401. Phase 3 and Phase 4 both prove the naming works — the refusal is computed
pre-flight, client-side, which is the only place it *can* be specific, since over the wire the
gateway has nothing but a 401 to offer.

Having done that work, the presentation layer then discards the actionable half. A user hitting
this sees a sentence that stops mid-clause and no way forward, which is close to the state the
outcomes were built to replace. The failure is recoverable in one command and that command is
the part that got cut.

## Evidence

**Verified:** observed directly during Phase 4 by the Product Owner, who reported reading only
as far as `…records for it`. The full message text is in `DeviceAuthPolicy.swift` and is known
to be longer. Independently, the `-1011` string was seen clipped with a trailing ellipsis in
the Phase 1 window.

**Not verified:** which view modifier is responsible, or whether it is truncation
(`lineLimit`) versus a non-wrapping frame. Not investigated — the fix belongs with whoever
owns that view.

## What to do

Let the message wrap, and prefer the multi-line form for anything carrying a remedy. Worth
deciding at the same time whether a copyable affordance for the embedded command is warranted,
since the recovery step is a shell command the user must retype exactly.

Cheap, but do not treat it as cosmetic: it is the difference between a self-explaining failure
and an unexplained one, and it silently negates work already paid for.
