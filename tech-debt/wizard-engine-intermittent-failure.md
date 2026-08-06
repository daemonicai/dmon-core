# `WizardEngineTests` intermittent failure

**Status:** open — observed once, not reproduced since, not investigated
**Where:** `test/Dmon.Core.Tests/Rpc/WizardEngineTests.cs` — `InvalidChooseOneAnswer_RePromptsStep`; exercises `core/Dmon.Core/Rpc/ProviderSetupHandler.cs`
**Surfaced:** 2026-08-02, during `dmon-home-foundations` (unrelated to that change — no block touched .NET source)
**Severity:** unknown, which is the reason to look
**See also:** [`Dmon.Core.Tests` has a recurring intermittent failure](dmon-core-tests-intermittent-failure.md) — a second, **unidentified** sighting in the same assembly (2026-08-06). Same shape, but the failing test was not captured, so the two are **not** assumed to share a cause. If that one is ever identified as this test, merge the notes.

## What

The test failed once under a full `make test` run, then passed standalone and on
every rerun since.

## Why it deserves a look rather than a shrug

The obvious reading is "flaky test". The less comfortable reading is that **an
invalid answer intermittently failing to re-prompt is a real ordering or
async-timing defect in the wizard**, not a test artifact — and the wizard is a
user-facing setup flow where a swallowed re-prompt looks like the app ignoring
you.

The two readings are distinguishable by looking; they are not distinguishable by
rerunning until green, which is what has happened so far.

## What to do

Investigate on its own terms, outside any `home/` work. The approach that worked
for the two `home/` flakes this session: **do not sample it — force the
mechanism.** Find the suspected interleaving, inject a delay that makes it
deterministic, and see whether the failure reproduces 100%. If it does, it is a
defect. If no injection can produce it, that is genuine evidence for a test
artifact rather than an absence of evidence.
