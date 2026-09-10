# Three `Dmon.Terminal.Tests` tests hang

**Status:** open
**Where:** `test/Dmon.Terminal.Tests/`, specific tests **not yet identified**
**Surfaced:** 2026-09-10, while measuring which tests write into `~/.dmon/sessions`
(`session-root-resolution`)
**Severity:** medium. It blocks `make test` from completing reliably, and it hides any
failure in the tests that hang.

## What (verified)

Two full-suite runs on `main` @ `1004b6c`, on the same machine, the same morning:

| Run | Terminal result | Duration |
|---|---|---|
| `env -u MEKO_API_KEY make test` | Passed 194/194 | **17 m 2 s** |
| same command plus `--blame-hang-timeout 5m` | Passed 191, then **no activity for 5 min**, hang dump taken, **run aborted** (exit 1) | 2 s + 5 min |

A third run the same morning (`make test`, block 1A gates of `session-root-resolution`)
passed **194/194 in 19 s**. So the hang is **intermittent**, not constant. When it
happens, three tests either take many minutes or never finish. The first run's 17 minutes
suggests the former: they eventually complete, perhaps by timing out internally.
Every other assembly in the same runs finished in seconds (Core: 52 s).

A hang dump was taken (`dotnet_38722_…_hangdump.dmp`) but not analysed, because
`dotnet-dump` is not installed. It was left in a session scratchpad and has not been
kept.

## Also observed once (unverified relation)

On the same morning, a filtered `Dmon.Core.Tests` run
(`Category!=Live&FullyQualifiedName!~LiveToolCallE2ETest`) sat for more than
6 minutes at 0% CPU with no child processes, and was killed. Unlike the Terminal
hang, it **did** coincide with the dmon-home app running (its core held
`mlx_lm.server` on port 8666), so it may have been interference. Reruns without
dmon-home did not reproduce it. It may also be the same thing as
[the `Dmon.Core.Tests` intermittent failure](dmon-core-tests-intermittent-failure.md).

## What to do

1. **Name the tests first.** Run
   `dotnet test test/Dmon.Terminal.Tests -c Release --blame-hang-timeout 2m --logger "console;verbosity=normal"`.
   The tests missing from the passed list are the ones hanging. The blame sequence
   file also records the test in flight when the dump was taken.
2. Candidates, from a grep for tests that start real processes (**not** confirmed):
   `CoreProcessManagerRestartTests`, `InitCommandTests` / `InitFeedFixture` (a
   package-feed restore can wait on the network).
3. Decide per test whether the wait can succeed **at all** in this environment
   before widening any timeout. See the flaky-test guidance recorded for this repo.
