# `make clean` cleans neither Swift tree

**Status:** open
**Where:** `Makefile` — the `clean` target
**Surfaced:** 2026-08-05, by the section-4 supervisor (pre-existing)
**Severity:** trivial

## What

`clean` is `rm -rf build/`, which misses:

- `daemon/Daemon.App/.build/` (pre-existing)
- `home/.build/` (SwiftPM)
- `home/.build-xcode/` (the app target's derived data)

So a "clean" build leaves both Swift trees intact.

## Why it is worth recording anyway

Only because it is now a **known gap rather than a discovery**. The `home/`
targets added in section 2 are *consistent* with the existing `Daemon.App`
behaviour rather than introducing an inconsistency, which is why nobody has
fixed it.

The real cost is the failure mode when it bites: someone chasing a stale-build
problem runs `make clean`, gets a clean result, and concludes the problem is in
their code. Stale-artifact confusion is expensive precisely because the person
hitting it has already ruled out the true cause.

## What to do

Add both `home/` paths and `daemon/Daemon.App/.build/` to the `clean` target.

**Care needed on one point:** task 3.3's and 4.7's human verification recipes
hard-code paths under `home/.build-xcode` derived from `-configuration Release`
and `-derivedDataPath home/.build-xcode`. Deleting that tree is fine; *changing*
where it lives is not, and breaks both recipes in the Product Owner's hands
mid-verification. Change either flag and both recipes move in the same commit.
