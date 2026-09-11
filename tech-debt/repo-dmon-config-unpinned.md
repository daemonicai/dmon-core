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

## Why pinning the repo root would not fix it

The obvious remedy, adding `sessionStore: local` to the repo's `.dmon/config.yaml`, would
**not protect the tests**. The core reads `sessionStore` only from the `.dmon/` YAML files
in its **working directory** (`core/Dmon.Core/Hosting/DmonHostBuilder.cs:45-50`), not
from the root the resolver walks up to. A test host running in
`test/<Project>/bin/<Config>/net10.0/` would find the repo root but never read its pin.
This is the gap recorded in
[`sessionStore` is ignored from a subdirectory](session-store-setting-ignored-from-subdirectory.md)
and in ADR-004's amendment note. So this hazard **depends on that undecided question**:
if it is decided so that a root's own setting applies from its subdirectories, a repo
pin becomes a real remedy.

## What to do

1. **Test-side guard first.** For example, an analyzer or a base fixture that refuses to
   use the real resolver without a temp root that has an explicit `sessionStore: local`.
   That works whatever the subdirectory question decides.
2. Only after the subdirectory question is decided, consider pinning the repo root. Even
   then there is a catch: the repo is also a real project root for anyone running dmon in
   this checkout, and a pin would override their own global choice there. Today a pin
   would only take effect for a core whose working directory **is** the repo root.
