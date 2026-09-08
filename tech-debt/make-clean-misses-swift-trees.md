# `make clean` does not clean the Swift tree

**Status:** open
**Where:** `Makefile` — the `clean` target
**Surfaced:** 2026-08-05, by the section-4 supervisor (pre-existing)
**Severity:** trivial

## What

`clean` is `rm -rf build/`, which misses `daemon/Daemon.App/.build/`.

So a "clean" build leaves the Swift tree intact.

## Why it is worth recording anyway

Only because it is a **known gap rather than a discovery**. Nobody has fixed it
because nothing in the .NET flow trips over it.

The real cost is the failure mode when it bites: someone chasing a stale-build
problem runs `make clean`, gets a clean result, and concludes the problem is in
their code. Stale-artifact confusion is expensive precisely because the person
hitting it has already ruled out the true cause.

## What to do

Add `daemon/Daemon.App/.build/` to the `clean` target.

**Narrowed 2026-09-08 (ADR-038).** This note originally covered `home/`'s two
Swift trees as well, plus a caution about not moving the `-derivedDataPath` two
Product Owner verification recipes hard-code. `dmon-home` left this repository,
and its own `make clean` removes `.build`, `.build-xcode`, `.build-ios` and the
generated `.xcodeproj` — so that half is fixed there, and only `Daemon.App`
remains open here.
