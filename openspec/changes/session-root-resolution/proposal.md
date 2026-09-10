## Why

The documents disagree on what makes a directory a dmon project root. ADR-004's prose
and the `session-storage` spec say any `.dmon/` (spec: `.daemon/`) **directory** does,
"exactly as git does for `.git/`". ADR-004's own resolution algorithm and
`SessionDirectoryResolver` say only a `.dmon/config.yaml` **file** does. Because of that
gap, `LiveToolCallE2ETest`, which builds a temp root holding only `.dmon/config.local.yaml`,
has quietly written a session into the developer's real `~/.dmon/sessions` on every live run.
All 390 non-empty sessions found there on 2026-09-10 are this test
(`tech-debt/live-e2e-test-writes-into-home-session-store.md`).

The Product Owner settled the contradiction on 2026-09-10 in favour of the code: **a
project root is a directory containing `.dmon/config.yaml`.** The directory rule was
rejected because `ActiveModelStore` writes `./.dmon/config.local.yaml` on every model
switch. Under the directory rule, switching models in a fresh directory would quietly move
that project's sessions from the global store to a local one.

## What Changes

- Correct the `session-storage` spec's discovery requirement to state the rule the code
  already implements: walk up from CWD for `.dmon/config.yaml`; a `.dmon/` directory
  without `config.yaml` (for example, one holding only `config.local.yaml`) is **not** a
  root. Replace the stale `.daemon` naming with `.dmon`.
- Correct the first-use bootstrap scenario to match `BootstrapService`. When neither
  `~/.dmon/config.yaml` nor any ancestor `.dmon/config.yaml` exists, the core creates
  **`~/.dmon/`** (not `.dmon/` at CWD) with a default `config.yaml` and an empty
  `sessions/`.
- Add an amendment note to ADR-004 that resolves its internal contradiction in favour of
  its own algorithm (`:86`). The decision is unchanged; only the prose that contradicted
  it is corrected.
- Fix `LiveToolCallE2ETest` so its temp content root is a real project root, and assert
  that the session it creates lands inside that root. A regression then fails the test
  instead of quietly writing into `~`.
- Tag `LiveToolCallE2ETest` `[Trait("Category", "Live")]`, as `MekoLiveSmokeTests`
  already is, so it runs under `make test-live` and not under plain `make test`. Today
  it has no trait, so every `make test` with a provider key set makes a paid API call.
  That is how 390 of its sessions accumulated.
- Pin the rule with a `SessionDirectoryResolver` unit test: a `.dmon/` holding only
  `config.local.yaml` resolves to the global store.
- Give `CoreProcessFixture` the same marker, and make `IntegrationSmokeTest` assert
  that its created session lands in the fixture's root. A full-suite diff of the store
  (design D6) found this fixture to be the only other writer: its
  `SessionCreateReturnsNewSession` leaves an empty session in `~` on every `make test`,
  key or no key. That is the "empty twin" beside each live-test session.

No runtime behaviour changes. No user's sessions move.

## Capabilities

### New Capabilities

_None._

### Modified Capabilities

- `session-storage`: the *Session discovery — project-local by default* requirement is replaced (REMOVED + ADDED, because its scenario titles carry the stale `.daemon` naming) by *Session discovery — `.dmon/config.yaml` marks the project root*.
  Its root marker becomes `.dmon/config.yaml` (not a bare directory), its naming becomes
  `.dmon`, and its bootstrap scenario describes the global bootstrap the code performs.

## Impact

- **Specs:** `openspec/specs/session-storage/spec.md` (one requirement).
- **ADRs:** `docs/adrs/ADR-004-session-storage.md`, an amendment note only.
- **Tests:** `test/Dmon.Core.Tests/Integration/LiveToolCallE2ETest.cs` (marker,
  assertion, `Live` trait), `test/Dmon.Core.Tests/CoreProcessFixture.cs` and
  `Integration/IntegrationSmokeTest.cs` (marker, assertion), and
  `Session/SessionDirectoryResolverTests.cs` (the rule pin).
- **Production code:** none.
- **Out of scope:** stale `.daemon/` wording in the `auth` and `console-host` specs.
  That belongs to `tech-debt/docs-drift-pass.md`. Clearing the existing litter in
  `~/.dmon/sessions` is the owner's call and is not part of this change.
