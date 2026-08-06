# ADR-034 has no record of the Apple Silicon constraint

**Status:** open
**Where:** `docs/adrs/ADR-034-*.md`
**Surfaced:** 2026-08-05, section 3 of `dmon-home-foundations` — this is the *root cause* of task 3.4
**Severity:** medium — a binding constraint that is currently unwritten

## What

Confirmed by grep: **no** `arm64`, `metal`, `unified memory`, `apple silicon`,
`x86` or `intel` anywhere in ADR-034, which governs the MLX runtime
(`Dmon.Providers.Mlx`, the Daemon's triage/escalation pair, and the future
speech path).

The requirement that makes Intel impossible existed in reality and nowhere in
writing, so nothing enforced it — which is precisely why an `x86_64` slice was
being produced until task 3.4 restricted the app target.

ADR-037 D5 now records it **for `dmon-home`**. ADR-034 still does not record it
for the runtime itself.

## Why it matters

The next consumer that isn't `dmon-home` hits the same silence and re-derives
the same fact: a sidecar bucket decision, CI runner selection, or the release
matrix asking which artifacts are arch-restricted.

## What to do

An in-place **amendment note** on ADR-034 — one file, no new ADR.

This one *genuinely is* framing-only under the ADR-028 precedent: it states a
physical fact that was always true and changes no decision. That is the
instructive contrast with the arm64 blocker itself, where the same instrument
would have been wrong because a decision was actually being made.

## Keep it separate from the docs pass

[The docs drift pass](docs-drift-pass.md) is stale *metadata* — a wrong count, a
mismatched status label, an orphaned summary. This is a **binding constraint
that is unwritten**. Different kind of problem, different urgency, should not be
bundled into a tidy-up.
