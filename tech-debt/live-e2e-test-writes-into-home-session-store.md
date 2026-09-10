# The live e2e test writes into the user's home session store

**Status:** open
**Where:** `test/Dmon.Core.Tests/Integration/LiveToolCallE2ETest.cs:57-61`, against
`core/Dmon.Core/Session/SessionDirectoryResolver.cs:47`
**Surfaced:** 2026-09-10, while checking the
[aborted-create orphan](aborted-create-orphans-session-directory.md)
**Severity:** low for correctness, but it pollutes real user state on every live test run

## What

`LiveToolCallE2ETest` creates a temp content root and writes its provider config
to `<tmp>/.dmon/config.local.yaml`. `SessionDirectoryResolver.FindDmonRoot` treats
a directory as a dmon root only if it contains `.dmon/config.yaml`. The temp
directory therefore never counts as a root, and the resolver falls back to the
global `~/.dmon/sessions`.

The test cleans up its temp directory in a `finally` block, but the session was
written somewhere else and is never deleted. **Every `make test` run with a
provider key set leaves a session in the developer's real home store.**

## Evidence (verified)

- All 390 non-empty sessions in `~/.dmon/sessions` (as of 2026-09-10) are this
  test's prompt, verbatim: *"Use your read_file tool to read the file at path
  ./marker.txt…"*. None is a real user session.
- Before `lazy-session-creation` (#115), each run left **two** directories: the
  real session plus an empty twin created within about a second. That accounts
  for most of the 455 empty directories there, **but not all**. The store is also
  the fallback for real hosts. On 2026-09-10 the dmon-home app
  (`DmonHomeApp` → `ndmon` → core, working directory `dmon-home`, no
  `.dmon/config.yaml`) created sessions there while this was being measured.
  Empty sessions in `~/.dmon/sessions` therefore **cannot** be assumed to be test
  output.
- The test has no `[Trait("Category", "Live")]`, so plain `make test` runs it (and
  makes a paid API call) whenever a provider key is set. That is why the sessions
  accumulated so quickly.
- On `main` at `610f5db`, one run of the test left **one** directory
  (`6f94be14-…`, with content) and no twin. #115 removed the twin, but the
  pollution remains.

## Why it matters

- Tests should not write to real user state. A developer's `~/.dmon/sessions`
  fills with sessions they never created, and `session.list` returns them.
- It skewed the empty-session litter measurement (426 of 787 were cited in
  `lazy-session-creation`'s proposal) that fed that change's design D1. D1 is
  still justified by the repo-store evidence, but the home-store figure was
  measuring the test suite rather than users.

## What to do

1. Isolate the test's session store. Either write `.dmon/config.yaml` (not
   `config.local.yaml`) so the temp root is recognised as a dmon root, or set
   `sessionStore` to a path inside the temp root. Then assert that the session
   landed *inside* `contentRoot`, so a regression fails the test rather than
   quietly writing back into `~`.
2. Check the other tests that spawn a core (`CoreProcessFixture` users,
   `Dmon.Network.Tests`) for the same fallback.
3. After the fix, the test's sessions can be pruned from `~/.dmon/sessions`.
   **Do not clear the whole store**: real hosts write there too. Only sessions
   whose first message is the test's `marker.txt` prompt, plus the empty twin
   created within about a second of each, are identifiably test output. Pruning
   is the owner's call, not an automatic step.
