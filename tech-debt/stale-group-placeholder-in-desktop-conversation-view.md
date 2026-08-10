# Stale `Group 5` placeholder comment on a populated Desktop view

**Status:** open
**Where:** `frontends/Dmon.Desktop/Views/ConversationView.axaml.cs:8`
**Surfaced:** 2026-08-10, by the section-9 supervisor of `dmon-home-foundations` (pre-existing, out of that change's scope)
**Severity:** trivial

## What

The file-header comment calls the view a *"Placeholder view … Group 5 adds content"*.
`ConversationView.axaml` is 281 lines of real content. The comment is a
forward-looking promise about work that has since landed — a false statement about
the present, not a locative note about which change-group built the thing.

## Why it is worth recording

This is the **same defect class** `dmon-home-foundations` §9 existed to remove from
`core/Dmon.Protocol/Gateway/ControlFrames.cs`, found in a different bucket while
sweeping for it. §9 corrected two annotations that said "not enforced until Group 6"
and "dedup logic is Group 5" when both were implemented.

The distinction that matters when fixing it — and the reason the rest of the repo's
~20 `Group N` references were deliberately left alone — is **locative vs temporal**:

- *Locative* ("Group 6 / 6.2 built this") stays true forever. Leave it.
- *Temporal* ("not until Group 6", "Group 5 adds content") is a claim about the
  present that expires the moment the work lands. Fix it.

The cost is small but real: a reader who trusts the header treats a populated view as
scaffolding, and either duplicates it or hesitates to edit it.

## What to do

Rewrite the header to describe what the view *is*. One line, no behaviour change, no
spec delta. Belongs to whichever change next touches `Dmon.Desktop` — it is not worth
a change of its own.

Provenance note: **verified**, not inferred — the supervisor read both the comment and
the `.axaml` it describes. Nobody has yet swept the rest of `frontends/Dmon.Desktop`
for sibling instances; that sweep is unowned.
