# Three `Dmon.Terminal.Tests` tests hang

**Status:** resolved on branch `fix/terminal-tests-hang` (2026-09-11); the merge commit is
recorded here once merged.
**Where:** `test/Dmon.Terminal.Tests/InitFeedFixture.cs:54-92` (`RunAsync`), shared by the
three `InitCommandTests`
**Surfaced:** 2026-09-10, while measuring which tests write into `~/.dmon/sessions`
(`session-root-resolution`)
**Severity:** medium. It blocks `make test` from completing reliably, and it hides any
failure in the tests that hang.

## Resolution (2026-09-11)

All six copies of the shape now call one helper, `test/Shared/ProcessRunner.cs`, which is
compiled into both `Dmon.Core.Tests` and `Dmon.Terminal.Tests` as a linked file. It applies
both measures from the fix list below, and each one works on its own:

1. It sets `MSBUILDDISABLENODEREUSE=1` on every process it starts, so no worker node
   outlives `pack-core.sh`.
2. It never waits unboundedly for EOF. Output is pumped into a snapshot; after the process
   exits, the drain gets a 10 s grace. If a descendant still holds the pipe, the helper
   returns what it has with `OutputTruncated = true`: a zero exit still passes, and a
   non-zero one fails with a note that the output was cut short.
   `ProcessRunnerTests` proves this in isolation with `bash -c 'sleep 30 & echo $!'`.

**Forced both ways (verified 2026-09-11)**, from zero MSBuild nodes each time, node reuse on
in the outer environment, `--filter InitCommandTests`:

| Tree | Result |
|---|---|
| `main` @ `ac1fa98` (before) | hung; aborted at the 2 min hang timeout; a `nodeReuse:true` MSBuild node was alive afterwards |
| the fix | **3/3 in 7 s**; no MSBuild node afterwards |
| the fix with measure 1 disabled (measure 2 alone) | **3/3**, ~12 s slower (one drain grace used up); a `nodeReuse:true` node **was** alive afterwards, so the hang condition was present and was survived |

Measure 1 alone is the `MSBUILDDISABLENODEREUSE=1` row of the forcing table below. Two
full `dotnet test Everything.slnx` runs **without** the workaround variable then passed
every assembly, about 70 s each (Terminal 194/194 in 24-25 s, Core 629/630 with 1 skip).

**A correction to the fix list below.** Option 2's "read with `OutputDataReceived` and stop
reading once the process has exited" does **not** work. Since .NET 5,
`Process.WaitForExitAsync` (and parameterless `WaitForExit()`) also waits for EOF on streams
read in that async mode, so it reproduces the hang. The helper pumps with `ReadAsync`
instead, and says so in a comment.

**Left alone, deliberately:**
- The long-running `dotnet run`/`dotnet exec` readers (`RunAndReadAgentReadyAsync` in
  `InitCommandTests` and `CompositionRootTests`, `FileBasedProgramLaunchTests`,
  `CoreProcessFixture`, `LegacyExtensionsListIgnoredIntegrationTest`). They read lines under a
  token and kill the tree, which is a different shape, and none of them builds.
- **Production has the same shape, not reproduced.** `CoreProcessManager.BuildFileBasedProgramAsync`
  (`core/Dmon.Runtime/CoreProcessManager.cs:186-192`) runs `dotnet build <Dmon.cs>` and then
  awaits `ReadToEndAsync` bounded only by the caller's token, and the Terminal host's token
  has no deadline (`frontends/Dmon.Terminal/Program.cs:30-37`). One forcing attempt
  (`FileBasedProgramLaunchTests`, zero nodes, reuse on) passed 2/2 in 13 s with **no** MSBuild
  node left behind. The inference, not verified: a single-project file-based build does not
  start an out-of-process worker node, while `pack-core.sh`'s multi-project packs do. If a
  `Dmon.cs` ever gains project references, or MSBuild changes that behaviour, this becomes
  the same hang at host startup.

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

**Why it is intermittent (inferred, not verified):** the hang needs a node that the fixture's
own `pack` **spawned**. If `pack` connects to a node that already exists, no new holder
inherits the pipe. But "the run started with no nodes" does not predict it. In the first
plain run below, the `dotnet test` build had already started nodes, and Terminal still
hung; `Dmon.Core.Tests`' identical fixture (see below) ran in the same run and did not.
Which fixture's `pack` ends up spawning a node, rather than reusing one, depends on node
availability and handshake at that moment. The one **verified** reproduction is the
standalone `--filter InitCommandTests` control, from zero nodes.

**How it was forced**, on the `change/session-root-resolution` branch, from zero MSBuild nodes each time:

| Run | Result |
|---|---|
| full `dotnet test Everything.slnx … --blame-hang-timeout 3m` | Terminal 191/194, then 3 min of inactivity, hang dump, run aborted |
| `dotnet test test/Dmon.Terminal.Tests … --blame-hang-timeout 2m`, per-test output | the three `InitCommandTests` are the only tests missing from the completed list |
| `--filter InitCommandTests`, `MSBUILDDISABLENODEREUSE=1` | **3/3 passed in 7 s** |
| `--filter InitCommandTests`, reuse on (the control) | hung, aborted at 2 min; an `MSBuild.dll` node started **during the fixture** was still alive afterwards |
| two full suites with `MSBUILDDISABLENODEREUSE=1` | Terminal **194/194 in 21-22 s** both times |

## Fix (as recorded before the resolution; see the correction above)

It needs a test-code change, which was out of scope for `session-root-resolution`.
Either measure works alone; doing both is more robust:

1. Stop build nodes outliving the script: set `MSBUILDDISABLENODEREUSE=1` in the
   fixture's `ProcessStartInfo.Environment` (the variable the forcing runs used), or pass
   `--disable-build-servers` to the `dotnet pack` calls (not tested here).
2. Stop the fixture waiting forever for EOF: give the stream reads the same deadline as
   the exit wait, or read with `OutputDataReceived` and stop reading once the process has
   exited.

Apply the same fix to every copy of the shape:

- `test/Dmon.Core.Tests/Composition/ComposedCoreFeedFixture.cs:54-92`: a line-for-line
  twin of `InitFeedFixture.RunAsync` (it runs `pack-core.sh`, waits 5 min for exit, then
  awaits stdout with no timeout). It is shared by `CompositionRootTests`,
  `FileBasedProgramLaunchTests` and `PackagingChecksTests`. **Fix it in the same change**,
  or the Core side keeps the hang.
- `InitCommandTests.RunDotnetAsync` (lines 112-155), for its own `dotnet` calls.
- `Composition/CompositionRootTests.RunDotnetAsync` (lines 141-175), the same shape again.
- `Packaging/ToolPackTests.cs` and `Composition/VersionRangeRestoreTests.cs`, which also run
  `dotnet` with redirected output and `ReadToEndAsync`; check each one.

**Workaround until then:** run the suite with `MSBUILDDISABLENODEREUSE=1` exported. No
longer needed once the resolution above is merged.

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
dmon-home. The leading suspect is `Composition/ComposedCoreFeedFixture.cs`, the Core
twin of `InitFeedFixture` (see the fix list above). `Packaging/ToolPackTests.cs` and
`Composition/VersionRangeRestoreTests.cs` have a similar shape. That is a lead only; the
Core hang has not been forced. See
[the `Dmon.Core.Tests` intermittent failure](dmon-core-tests-intermittent-failure.md).
