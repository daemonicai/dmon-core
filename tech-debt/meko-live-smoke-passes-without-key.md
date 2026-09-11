# `MekoLiveSmokeTests` passes silently without its key

**Status:** open
**Where:** `test/Dmon.Memory.Meko.Tests/Meko/MekoLiveSmokeTests.cs:47-55`
**Surfaced:** 2026-09-10, `session-root-resolution` block 2B gates (verified: read the code, and
observed the result)
**Severity:** low, but it is exactly the silent pass this repo's live tests are meant to rule out

## What

The test is tagged `[Trait("Category", "Live")]`, but it is a plain `[Fact]` that
`return`s early when `MEKO_API_KEY` is absent. So `make test-live` reports it as
**Passed** (15 ms) when it has tested nothing. Observed on 2026-09-10 with the key unset
(the recommended `env -u MEKO_API_KEY` to avoid its ~90 s hang).

Contrast with `LiveToolCallE2ETest`, which is a `[SkippableFact]` that calls `Skip.If`
when no key is present. Its doc comment states the rule: *"The test SKIPS — never
silently passes — when no provider key is present."* A skipped test is visible in the
run summary; a silent pass is indistinguishable from a real one.

## What to do

Convert it to `[SkippableFact]` and `Skip.If(string.IsNullOrWhiteSpace(apiKey), …)`, the
same pattern as `LiveToolCallE2ETest`. Check the `Xunit.SkippableFact` package is
referenced by `Dmon.Memory.Meko.Tests`.
