## 1. Dependency adoption (GATED)

- [x] 1.1 Confirm dcli has published a release containing `InputRequest.AllowBack` (the `back-nav-input` change) to the package source. If not yet published, STOP — this change cannot proceed until the dcli release is available.
- [x] 1.2 Bump the `Dcli` PackageReference in `src/Dmon.Terminal/Dmon.Terminal.csproj` from `0.2.0-rc.2` to the published release identified in 1.1.
- [x] 1.3 Bump the `Dcli.Testing` PackageReference in `test/Dmon.Terminal.Tests/Dmon.Terminal.Tests.csproj` to the same version.
- [x] 1.4 Restore and confirm the solution compiles against the new package (`dotnet build Dmon.slnx -c Release`).

## 2. Activate text-input back-navigation

- [x] 2.1 In `ConsoleEventHandler.RenderTextInputAsync` (`src/Dmon.Terminal/ConsoleEventHandler.cs`), set `AllowBack = true` on the `InputRequest` used for the free-text/secret step.
- [x] 2.2 Confirm the existing `DialogOutcome.Back → WizardAnswerOutcome.Back` mapping in that method remains in place and is reached (no plumbing change expected).

## 3. Render choose-many as a true multi-select

- [x] 3.1 Rewrite `ConsoleEventHandler.RenderChooseManyAsync` to call `Terminal.MultiSelectAsync(MultiSelectRequest { ..., AllowBack = true })` instead of the single-select stand-in.
- [x] 3.2 Encode the returned `DialogResult<int[]>` value as the comma-separated zero-based indices wire format consumed by `WizardAnswerHelper.DecodeChooseManyIndices`; map `DialogOutcome.Back`/`Cancelled` to the `Back`/`Cancel` outcomes as the other renderers do.
- [x] 3.3 Remove the obsolete "render as single-select" comment and any now-dead single-select handling in that method.

## 4. Tests

- [x] 4.1 Add a renderer test (via `FakeTerminal` in `test/Dmon.Terminal.Tests/Fakes`) asserting that a `TextInputStep` whose dialog returns `DialogOutcome.Back` produces a `WizardAnswerCommand` with outcome `Back`.
- [x] 4.2 Add a renderer test asserting that a `ChooseManyStep` whose `MultiSelectAsync` returns multiple checked indices produces an `Answered` `WizardAnswerCommand` whose value is those indices joined comma-separated.
- [x] 4.3 Add a renderer test asserting that a `ChooseManyStep` whose dialog returns `DialogOutcome.Back` produces a `Back` outcome.
- [x] 4.4 Extend `FakeTerminal` with `MultiSelectAsync` support if it does not already expose it, so the above tests can drive it.

## 5. Validation

- [x] 5.1 `make build` is clean (no warnings; `TreatWarningsAsErrors`).
- [x] 5.2 `make test` (or `dotnet test -c Release`) is green — new tests and all existing tests.
- [x] 5.3 `openspec validate adopt-dcli-back-nav --strict` passes.
