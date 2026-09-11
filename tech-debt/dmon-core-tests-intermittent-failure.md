# `Dmon.Core.Tests` has a recurring intermittent failure

**Status:** open — recurrence established, cause unknown, failing test **not identified**
**Where:** the `Dmon.Core.Tests` assembly, under a full parallel `env -u MEKO_API_KEY make test`
**Surfaced:** 2026-08-06, during `dmon-home-foundations` section 6 (unrelated to that change — block 6.2 touches no .NET source, and the same tree passed the full suite twice earlier the same day)
**Severity:** unknown, which is the reason to look

## What was observed

One full-suite run reported `Failed: 1, Passed: 612, Skipped: 1` in `Dmon.Core.Tests`.
The immediate re-run passed, as did **three further dedicated full-suite runs** — roughly
**one failure in eight full runs** that day.

**The failing test's name was not captured.** The gate command grepped for the per-assembly
summary line, so the xunit `[FAIL]` line went nowhere, and it has not fired since. That is a
process defect on the observing side, not a property of the failure, and it is fixed below.

## Why this is its own note

There is a separate, *identified* sighting — [`WizardEngineTests` intermittent
failure](wizard-engine-intermittent-failure.md), 2026-08-02, same assembly, same
"failed once under a full run, passed on rerun" shape.

**Whether the two share a cause is unknown**, and this note exists precisely so that
they are not merged into one story on the strength of resemblance. The wizard test is
the obvious candidate; a plausible mechanism that predicts an observed failure is not
thereby its cause, and this change has already paid once for making that inference.
Folding this sighting into that note would have silently upgraded "something in
`Dmon.Core.Tests` failed" into "the wizard test failed again".

What the second sighting **does** establish, independent of identity: an intermittent
failure in this assembly is **recurring rather than a one-off**. That is what moves it
from a curiosity to something worth a session.

## Why it deserves a look rather than a shrug

`Dmon.Core.Tests` is the agent core's suite — 614 tests over the RPC surface, session
storage, tool dispatch and the permission model. A ~12% chance of a red run is already
enough to erode the gate's meaning: the habit it teaches is "rerun it", and that habit
is indistinguishable from the habit that ships a real intermittent defect. It will also
fire in CI, where a rerun is not free and the failure lands on someone who did not
cause it.

It may well be a test artifact. That is a finding to reach, not to assume.

## What to do

**First, catch it with its name attached.** The failure only appears under the full
parallel run, so a targeted single-assembly loop is unlikely to reproduce it. Loop the
full suite, preserving output, and stop on the first red run:

```sh
for i in $(seq 1 20); do
  echo "=== run $i ==="
  env -u MEKO_API_KEY make test 2>&1 | tee "/tmp/dmon-test-$i.log" | grep -qE '^Failed!' \
    && { echo "FAILED on run $i"; grep -E '\[FAIL\]' "/tmp/dmon-test-$i.log"; break; }
done
```

**Then force the mechanism rather than sampling it.** The approach that worked twice on
the `home/` flakes in this change: find the suspected interleaving, inject a delay that
makes it deterministic, and check whether the failure reproduces 100%. A clean run of
*n* proves almost nothing at a ~12% base rate — that underpowered comparison is exactly
what section 5 demonstrated before converting a rare flake into a demonstrated mechanism.

**If it turns out to be `InvalidChooseOneAnswer_RePromptsStep`**, merge this note into the
wizard one and inherit its reasoning about why a swallowed re-prompt would be a real
user-facing defect. **If it is a different test**, this note stays and the wizard note's
"observed once" status is still accurate.

## A separate symptom: a Core hang (2026-09-10)

`session-root-resolution`'s first `3.2` attempt **hung** (it did not fail) in
`Dmon.Core.Tests` for more than 9 minutes. On 2026-09-11, **one** full Core run with MSBuild
node reuse on (627 passed, 1 skipped) did not reproduce it. Two more full runs passed, but
they had `MSBUILDDISABLENODEREUSE=1` set, which switches off the suspected mechanism, so
they are not fair non-reproductions. The Terminal hang's cause has since been verified: a
fixture waits for stdout EOF while a reusable MSBuild node holds the pipe open. The leading
suspect here is `Composition/ComposedCoreFeedFixture.cs:54-92`, a line-for-line twin of
that fixture, shared by `CompositionRootTests`, `FileBasedProgramLaunchTests` and
`PackagingChecksTests`. `ToolPackTests` and `VersionRangeRestoreTests` have a similar
shape. That is a **lead** for the hang only. It says nothing about this note's one-off `Failed: 1`, which
is a different symptom. See [the Terminal hang](terminal-tests-hang.md).

**Update (2026-09-11):** the Terminal fix converted all of those Core sites too, so the
suspected mechanism is now closed in this assembly. Two full runs with node reuse **on**
then passed (629/630, 1 skip, 57-59 s). The Core hang itself was **never forced**, so
treat it as "probably the same cause, now fixed" rather than "verified fixed". A Core hang
seen after this merge is a new finding. The `Failed: 1` above is untouched: still open,
still unnamed.

## Provenance

Observed and measured by the Architect during section 6 of `dmon-home-foundations`
(block B2's gate run). The run counts above are **verified**; the attribution to any
particular test is **not attempted**. Detail in that change's `DEVLOG.md` while it
remains unarchived.
