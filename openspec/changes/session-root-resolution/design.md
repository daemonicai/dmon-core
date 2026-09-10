## Context

See proposal.md (Why) for the contradiction and the Product Owner's ruling.

Current behaviour, which this change keeps:

- `SessionDirectoryResolver.FindDmonRoot` walks up from the working directory for
  `.dmon/config.yaml`. With no root it returns `~/.dmon/sessions`. With a root, it reads
  `sessionStore` (`local` by default, or `global`, or a path).
- `BootstrapService` runs at core start. If neither `~/.dmon/config.yaml` nor an ancestor
  `.dmon/config.yaml` exists, it creates `~/.dmon/` with a default `config.yaml`
  (`sessionStore: local`) and `sessions/`.
- `LiveToolCallE2ETest` creates `<tmp>/dmon-live-e2e-<guid>/`, writes
  `.dmon/config.local.yaml` (its provider pin), launches a real core with that directory
  as the working directory, runs `session.create` then `session.load`, and submits one
  turn. It deletes the temp directory in `finally`. It never looks at where the session
  was written, which is why the fallback went unnoticed.

## Goals / Non-Goals

**Goals:**
- Make every test that spawns a real core write its sessions inside its own temp
  directory, and make at least the live e2e test **prove** that it did.
- Pin the root-marker rule with a unit test so the prose and the code cannot drift apart
  again without a red test.

**Non-Goals:**
- Changing the resolver, the bootstrap, or any configuration layering.
- Redirecting `HOME` for tests. Bootstrap and global config reads touching the real
  `~/.dmon/config.yaml` are read-mostly and out of scope. This change is about **session
  writes** into the real store.
- Pruning existing litter.

## Decisions

### D1 — The root marker is `.dmon/config.yaml` (Product Owner, 2026-09-10)

This is ADR-004's own algorithm, and it is what the code already does. The rejected
alternative, "any `.dmon/` directory", conflicts with the `config.local.yaml` layer.
`ActiveModelStore` creates that file on a model switch, so the directory rule would let a
model switch relocate a project's sessions. The spec delta states the rule as a SHALL,
with a negative scenario for the `config.local.yaml`-only case.

### D2 — The live test gets a minimal `config.yaml` marker; its provider pin stays in `config.local.yaml`

Write `<contentRoot>/.dmon/config.yaml` containing only `sessionStore: local`, and keep
the provider stanza in `config.local.yaml` unchanged.

*Alternative considered:* move the provider stanza into `config.yaml`. That would work
too, since project `config.yaml` also outranks `~/.dmon/config.yaml`. But it changes two
things at once, and it gives up the "highest-priority layer" guarantee the test's comments
rely on. The minimal marker changes one thing: root detection.

### D3 — The test asserts the session landed inside its content root

After the turn completes, and **before** the `finally` block deletes the temp directory,
the test SHALL assert that `<contentRoot>/.dmon/sessions/` contains a session directory
whose `messages.jsonl` is non-empty. This check is what turns the fix from "happens to
work" into a regression guard. Without it, removing the marker would send sessions back
to `~` while the test stays green.

*Alternative considered:* snapshot `~/.dmon/sessions` before and after, and assert
nothing was added. Rejected: that store is shared with every other process the developer
runs, and with parallel test assemblies, so an exact-count check on it would be flaky.
Asserting that the session is **present in the isolated root** discriminates the same
failure without depending on shared state.

### D4 — The resolver rule is pinned in the existing `SessionDirectoryResolverTests`

Add the negative case beside the existing tests in
`test/Dmon.Core.Tests/Session/SessionDirectoryResolverTests.cs`: a temp tree whose
`.dmon/` holds only `config.local.yaml` resolves to the global path. Pair it with the
positive case, where adding `config.yaml` makes the same tree resolve locally, so the
test shows that the marker file is what makes the difference.

### D5 — The ADR-004 fix is an amendment note, not a superseding ADR

The decision does not change. ADR-004's algorithm (`:86`) already says `config.yaml`;
only its prose (`:80`, `:90`) contradicts it. This follows the repo's existing amendment
style (blockquote under the title, as in ADR-012/017/018), dated and attributed to this
change. The note states that `.dmon/config.yaml` is the root marker, and that `:80`/`:90`'s
"`.dmon/` directory" means a directory containing that file.

### D7 — The live e2e test is tagged `Live` (Product Owner, 2026-09-10)

`LiveToolCallE2ETest` gets `[Trait("Category", "Live")]`, matching
`MekoLiveSmokeTests`. `make test` filters `Category!=Live` and `make test-live` selects
`Category=Live`, so the tag moves the test from every ordinary run to the live target.
The test still skips (per ADR-005) when no key is set. D2/D3 are still needed: the tag
only reduces how often the test runs, while the marker and assertion make each run
write to the right place and prove it.

*Consequence:* the D3 assertion only executes under `make test-live` with a key set.
The block that lands it must run that target, not only `make test`, or the new
assertion is never exercised.

### D6 — Which other tests are in scope is decided by measurement, not inspection

Nineteen test files start a real core. Instead of auditing each one, `~/.dmon/sessions`
and the repo's `.dmon/sessions` were snapshotted before a full
`env -u MEKO_API_KEY make test` with provider keys set, and diffed afterwards. Only
tests implicated by that diff are in scope. The result is recorded in tasks.md.

The first such run was **contaminated**: the dmon-home app's core was writing to
`~/.dmon/sessions` during it, so an extra empty session could not be attributed. The
measurement that counts was re-run with no dmon host process alive (checked by process
list and by port 8666 being free).

**Result (2026-09-10, `main` @ `1004b6c`):** exactly two tests write into
`~/.dmon/sessions`, and both were confirmed by running them alone:

| Test | Writes | Why |
|---|---|---|
| `LiveToolCallE2ETest` | a session with content, plus `.lock` | temp root holds only `config.local.yaml` |
| `IntegrationSmokeTest.SessionCreateReturnsNewSession` | an empty session, no `.lock` | `CoreProcessFixture`'s temp root holds only `appsettings.json` |

The second is the "empty twin" seen beside every live session. It is a separate test
that runs alongside the live one, not a second session from the same run. No other
assembly wrote to either store. The repo's `.dmon/sessions` was untouched.

### D8 — `CoreProcessFixture` gets the same marker, and `IntegrationSmokeTest` gets the regression guard

`CoreProcessFixture` writes `<CoreDir>/.dmon/config.yaml` (`sessionStore: local`)
beside its `appsettings.json`. That isolates every test using the fixture
(`IntegrationSmokeTest`, `ConsoleSmokeTest`), not just the one measured writing. `ConsoleSmokeTest` sends `session.list`, which today **reads** the
developer's real store. After the fix it reads the fixture's own empty one.

`SessionCreateReturnsNewSession` then asserts that
`<CoreDir>/.dmon/sessions/<session.id>/meta.json` exists. Unlike D3's assertion, this
one runs on every plain `make test` and in CI, with no key needed, so it is the
change's main regression guard. D3 still covers the live path.

`LegacyExtensionsListIgnoredIntegrationTest` does not use the fixture. It launches
its own core in its own temp directory and already writes a `config.yaml` there, so
it is already a project root and is out of scope. That is consistent with the
measurement.

## Risks / Trade-offs

- [The measured diff misses a test that writes to `~` only under conditions absent from
  the measuring run, such as a key that was not set] → The measuring run had
  `GEMINI_API_KEY` and `OPENAI_API_KEY` set. The spec's negative scenario plus D3's
  assertion guard the known case, and the tech-debt note stays as the record for
  anything else.
- [`config.yaml` in the temp root changes the effective config for the live test] → It
  contains only `sessionStore: local`. That is **not** inert (corrected after the
  section-1 supervisor review). The core's working directory is the temp root, so
  this project layer overrides any `sessionStore` in the developer's
  `~/.dmon/config.yaml`. That is the intended effect: it pins the store to the temp
  root whatever the developer's global setting is. The marker must therefore carry
  `sessionStore: local` explicitly (an empty or comment-only file would leave a
  global `sessionStore: global` in force). The core must be launched **with the temp
  root as its working directory**, because only then is that file layered in (see
  `tech-debt/session-store-setting-ignored-from-subdirectory.md`).
