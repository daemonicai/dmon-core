# The repo's own `.dmon/config.yaml` has no `sessionStore` pin

**Status:** open (latent: nothing triggers it today)
**Where:** `.dmon/config.yaml` at the repository root (tracked)
**Surfaced:** 2026-09-10, section-2 supervisor re-review of `session-root-resolution`
(inferred from the code; no test currently reaches it)
**Severity:** low

## What

The repository root carries a tracked `.dmon/config.yaml`, so the repo is a dmon project
root. A test binary runs from `test/<Project>/bin/<Config>/net10.0/`, which walks up to
that marker. The file sets no `sessionStore`, so the effective setting comes from the
developer's `~/.dmon/config.yaml`.

`session-root-resolution` established the rule that a test-owned root must pin
`sessionStore: local` explicitly, because an unpinned root follows the global setting.
The repo root breaks that rule. If a future test creates a session with its working
directory inside the repo (an in-process host that uses the real resolver without a
temp root), then on a machine with `sessionStore: global` its sessions go to that
developer's home store, and otherwise to the repo's own `.dmon/sessions`. Neither is
the test's own temp directory.

No test does this today: in-process session tests use temp roots or a `FakeResolver`.
The repo's historical `.dmon/sessions` litter (744 of 764 empty sessions dated
2026-05-25 to 06-14) may be an earlier instance, with the source long since removed.

## What to do

Decide whether the repo root should pin `sessionStore: local`. The catch: it is also a
real project root for anyone running dmon in this checkout, and a pin would override
their own global choice there. The alternative is a test-side guard, for example an
analyzer or a base fixture that refuses to use the real resolver without a temp root.
