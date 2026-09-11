# The live e2e test writes into the user's home session store

**Status:** resolved by change `session-root-resolution` (branch `change/session-root-resolution`; merge commit to be added after merge)
**Where:** `test/Dmon.Core.Tests/Integration/LiveToolCallE2ETest.cs:57-61`, against
`core/Dmon.Core/Session/SessionDirectoryResolver.cs:47`
**Surfaced:** 2026-09-10, while checking the
[aborted-create orphan](aborted-create-orphans-session-directory.md)
**Severity:** low for correctness, but it pollutes real user state on every live test run

## Resolution (2026-09-10)

This test was **one of three** writers; the change fixed all three.

| Writer | What it left in `~/.dmon/sessions` | Fix |
|---|---|---|
| `LiveToolCallE2ETest` (this note) | a session with content, on every run with a provider key | `sessionStore: local` marker, an assertion that the session landed in its temp root, and `[Trait("Category", "Live")]` so plain `make test` no longer runs it or makes a paid call |
| `IntegrationSmokeTest` via `CoreProcessFixture` | the empty "twin" beside every live session, on **every** `make test`, key or not | the same marker in the fixture, plus an assertion that runs in CI |
| `CoreProcessManagerRestartTests` | nothing on the measuring machine; a session on any machine whose global config sets `sessionStore: global` or a path | an explicit `sessionStore: local` in the `config.yaml` it already wrote |

The third writer was invisible to measurement, because its behaviour depended on the developer's global `sessionStore`. The section-2 supervisor found it by reading the code. The final check ran the suite a second time with an environment-supplied canary `sessionStore` path. That would expose any other test of that kind, meaning one whose core finds a root with no pin, provided the developer's `~/.dmon/config.yaml` sets no `sessionStore` (the environment is the lowest configuration layer). A core that finds no root always writes to `~/.dmon/sessions`, whatever the setting, and the home-store count catches that instead. See the change's `DEVLOG.md` §3.

The rule it established is recorded in the `session-storage` spec and in ADR-004's amendment note: `.dmon/config.yaml` marks a project root. Every test that launches a real core in a temp directory **and creates a session** must write that marker with an explicit `sessionStore: local`, and must keep the temp directory as the core's working directory. `LegacyExtensionsListIgnoredIntegrationTest` writes a marker without a pin; that is acceptable because it never creates a session.

The test sessions that had already accumulated were **not** pruned by the change. That remains the owner's call; see step 3 below.

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
