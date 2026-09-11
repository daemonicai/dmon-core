# Three `Dmon.Terminal.Tests` tests hang

**Status:** open. Tests named and cause verified (2026-09-11); not yet fixed.
**Where:** `test/Dmon.Terminal.Tests/InitFeedFixture.cs:54-92` (`RunAsync`), shared by the
three `InitCommandTests`
**Surfaced:** 2026-09-10, while measuring which tests write into `~/.dmon/sessions`
(`session-root-resolution`)
**Severity:** medium. It blocks `make test` from completing reliably, and it hides any
failure in the tests that hang.

## Cause (verified 2026-09-11)

The three tests are the `InitCommandTests`:
`Init_ExistingDmonCs_FailsWithNonZeroExit`, `Init_Scaffold_ContainsProtocolPinAndDmonHostCall`
and `Init_ScaffoldedDmonCs_BuildsAndEmitsAgentReady`. They share `InitFeedFixture`, whose
`InitializeAsync` runs `scripts/pack-core.sh` (several `dotnet pack` calls) through
`RunAsync`.

`RunAsync` redirects stdout and stderr, starts `ReadToEndAsync` on both, waits for the
process to exit (5-minute timeout), and then does `await stdoutTask` / `await stderrTask`
**with no timeout**. `ReadToEndAsync` completes only at EOF, which comes when **every**
holder of the pipe's write end has closed it. The `dotnet pack` calls start reusable MSBuild
worker nodes (`/nodeReuse:true`), and those inherit the pipe. When `pack-core.sh` exits,
a node is still alive and idle, holding the pipe open, so the fixture blocks until the
node's idle timeout ends it. That is about 15 minutes, which fits the 17-minute run below.

If a reusable node is **already** running when the fixture starts, `dotnet pack` connects
to it instead of spawning one, so no new holder inherits the pipe and the tests pass in
seconds. That is why the hang is intermittent. It reproduces whenever the run starts with
no nodes alive, for example right after `dotnet build-server shutdown`.

**How it was forced**, on the `change/session-root-resolution` branch, from zero MSBuild nodes each time:

| Run | Result |
|---|---|
| full `dotnet test Everything.slnx … --blame-hang-timeout 3m` | Terminal 191/194, then 3 min of inactivity, hang dump, run aborted |
| `dotnet test test/Dmon.Terminal.Tests … --blame-hang-timeout 2m`, per-test output | the three `InitCommandTests` are the only tests missing from the completed list |
| `--filter InitCommandTests`, `MSBUILDDISABLENODEREUSE=1` | **3/3 passed in 7 s** |
| `--filter InitCommandTests`, reuse on (the control) | hung, aborted at 2 min; an `MSBuild.dll` node started **during the fixture** was still alive afterwards |
| two full suites with `MSBUILDDISABLENODEREUSE=1` | Terminal **194/194 in 21-22 s** both times |

## Fix (not applied)

It needs a test-code change, which was out of scope for `session-root-resolution`.
Either measure works alone; doing both is more robust:

1. Stop build nodes outliving the script: set `MSBUILDDISABLENODEREUSE=1` in the
   fixture's `ProcessStartInfo.Environment` (the variable the forcing runs used), or pass
   `--disable-build-servers` to the `dotnet pack` calls (not tested here).
2. Stop the fixture waiting forever for EOF: give the stream reads the same deadline as
   the exit wait, or read with `OutputDataReceived` and stop reading once the process has
   exited.

`InitCommandTests.RunDotnetAsync` (lines 112-155) has the same shape for its own `dotnet`
calls, so apply the fix there as well.

**Workaround until then:** run the suite with `MSBUILDDISABLENODEREUSE=1` exported.

## History

Two full-suite runs on `main` @ `1004b6c`, on the same machine, the same morning
(2026-09-10):

| Run | Terminal result | Duration |
|---|---|---|
| `env -u MEKO_API_KEY make test` | Passed 194/194 | **17 m 2 s** |
| same command plus `--blame-hang-timeout 5m` | Passed 191, then **no activity for 5 min**, hang dump taken, **run aborted** (exit 1) | 2 s + 5 min |

A third run the same morning (`make test`, block 1A gates of `session-root-resolution`)
passed **194/194 in 19 s**.

### A different failure, same tests: stale sandboxed MSBuild nodes (2026-09-10)

During `session-root-resolution` block 2A's gates, an unsandboxed `make test` **failed**
(it did not hang) all three `InitCommandTests`, with `pack-core.sh failed (exit 1)`
("Operation not permitted" writing under `$TMPDIR`). `make build` failed the same way
(`MSB4018` in `CreateAppHost`). Seven long-lived MSBuild worker nodes plus
`VBCSCompiler` were alive, started around when subagents had run **sandboxed**
`dotnet build`/`dotnet test`. After `dotnet build-server shutdown`, the gates were green.

This was recorded at the time as a possible explanation for the hang. The 2026-09-11 runs
disprove that: every node in them was started by the run itself, unsandboxed. It is the
same root, though, **reusable build nodes outliving the process that started them**,
seen through a different symptom: a sandbox-bound node that cannot write, rather than a
live node that holds a pipe.

### Also observed (unverified relation)

On 2026-09-10 a filtered `Dmon.Core.Tests` run sat for more than 6 minutes at 0% CPU and
was killed while dmon-home was running. The same afternoon, `session-root-resolution`'s
first `3.2` attempt hung in `Dmon.Core.Tests` for more than 9 minutes **without**
dmon-home. Three Core test files have the same shape as `InitFeedFixture`, running `dotnet`
with redirected output and waiting for EOF with no reuse guard: `Packaging/ToolPackTests.cs`,
`Composition/CompositionRootTests.cs` and `Composition/VersionRangeRestoreTests.cs`.
That is a lead only; the Core hang has not been forced. See
[the `Dmon.Core.Tests` intermittent failure](dmon-core-tests-intermittent-failure.md).
