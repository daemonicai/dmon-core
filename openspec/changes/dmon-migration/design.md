## Context

`Dmon.Terminal` is the terminal-facing layer of dmon. It owns:
- Streaming LLM token rendering with separator rules.
- A status line showing model + mode + thinking-level.
- Inline wizard prompts (provider setup, model picker, tool confirm).
- A line-editor input mode that drives the RPC protocol to `dmon-core`.
- A markdown renderer for assistant turns and tool output.

It is built on **Spectre.Console** (`AnsiConsole.MarkupLine`, `AnsiConsole.Write(Rule)`, `Markup.Escape`, `Console.ReadKey`, direct `Console.Write` for cursor movements). Spectre's static-singleton `AnsiConsole` is the principal reason every non-`WizardEngine` file in this project is untested (validated in the §14.4 dcli postmortem; recorded in dcli memory `section14-api-ergonomics-findings`).

`dcli` v1 (the `core-rendering-architecture` change, archived `2026-05-28`) was scoped against this project. Its public façade — `ITerminal` with `IScrollback`/`IInput`/`IStatus`/`IAutocomplete` sub-surfaces, awaitable `SelectAsync`/`MultiSelectAsync`/`InputAsync`/`ChoiceAsync` dialogs, an `Events` channel — was designed to absorb exactly this project's needs. `dcli`'s `api-ergonomics-pass-1` change closes the three ergonomics gaps that surfaced when the wizard slice was ported in §14.4 (`Line.FromText`, `AllowBack`, secret-default masking).

The migration is sequenced after `api-ergonomics-pass-1` ships as `dcli 0.2.0-rc.x`. With those overloads in place, every file's port is straightforward.

## Goals / Non-Goals

**Goals:**
- Make every previously-untestable file in `Dmon.Terminal` unit-testable, by routing all rendering / dialog / input through `dcli`'s `ITerminal` (tier-A fake) or `Dcli.Testing.HeadlessTerminal` (tier-B harness).
- Preserve user-visible behaviour exactly — streaming output, wizard flows, tool-confirm prompts, input editing, copy-paste — no UX regressions.
- Drop the `Spectre.Console` dependency once the migration completes.
- Land in 5 phases, each gated on its own tests + reviewer audit + standard 4-gate sweep, single commit per phase per the dcli `CLAUDE.md` workflow.

**Non-Goals:**
- New UX features (any new behaviour gets its own change after this one).
- Changing the RPC protocol or `EventDispatcher` semantics.
- Replacing `CoreProcessManager` or `SlashCommandParser` — these are dmon-internal, not dcli's concern (Decision 3 of dcli's design: dcli = mechanics + rendering; dmon = data + semantics).
- Touching the `agent-core` capability or any non-terminal layer.
- Implementing `Scrollback.AppendRule` / incremental `Collapsible.AppendLine` / `PasteEvent` editor routing — those are dcli future-work; this change works around their absence.
- Re-fixing `DialogOutcome.Back` semantics for multi-select — `MultiSelectRequest` does not gain `AllowBack` in pass-1 by design (see dcli's `api-ergonomics-pass-1` Decision 4).

## Decisions

### 1. Phased migration, one capability surface at a time

Five phases, each one a single OpenSpec section in `tasks.md`, each landing as one `feat(dmon-migration): ...` commit:

1. **Phase 1 — Wire up `dcli`, port the renderer.** Add `dcli` + `Dcli.Testing` project refs. Port `TerminalRenderer.cs` first — it's the largest direct-Console-write user and the easiest to A/B against the old output. Verifies that token streaming + separator rules + status lines render correctly on the new substrate. Spectre still in use elsewhere; not yet removed.
2. **Phase 2 — Port the dialog surfaces.** `InlinePrompt.cs`, `ToolConfirmPrompt.cs`, `WizardRenderer.cs` (deleted), `WizardEngine.cs` (drops the test-quarantine indirection). Replaces all `Console.ReadKey`-blocking loops with `await`able dcli dialogs.
3. **Phase 3 — Port the event-routing adapter.** `ConsoleEventHandler.cs` becomes the thin adapter: RPC events → dcli calls. Delete `AddProviderCommand.cs`, `ReloadCommand.cs`.
4. **Phase 4 — Refactor `InputReader.cs`.** dmon's input layer becomes a state layer above `dcli.Input`, subscribing to `Events.InputSubmitted` / `InputChanged`. History + IsLocked + locked-buffer semantics stay in dmon.
5. **Phase 5 — Port `MarkdownRenderer.cs` and drop Spectre.** Markdown output type changes from `string` to `IReadOnlyList<Line>`. Remove `Spectre.Console` package reference. All dmon-terminal code now rides on dcli.

Why phased: each phase is independently reviewable, ships green tests, and keeps the project compileable + runnable. A failed phase doesn't strand the whole migration.

### 2. Tier-A fake `ITerminal` as the primary test substrate

Every newly-testable file in `Dmon.Terminal` (`TerminalRenderer`, `ConsoleEventHandler`, `InputReader` state layer) gets unit tests using a hand-rolled fake `ITerminal` (mirroring `dcli`'s `tests/Dcli.Tests/FakeTerminalTests.cs` pattern: record command-side calls, drive consumer-style code with synthesized `KeyEvent` / `DialogResult`). Tier-B `HeadlessTerminal` is reserved for the small number of integration tests that need the real loop (e.g. testing wizard back-stack behaviour against real dcli dialogs).

Why: tier-A fakes are cheap, fast, and zero-dependency. Tier-B is for the cases where the seam between dmon and dcli is itself under test.

### 3. Preserve wizard back-stack semantics byte-for-byte

The §14.4 dmon-wizard port at `dcli/samples/Dcli.Demo.DmonWizard/` already validated the wizard flow on dcli. **That port is the reference implementation** for the wizard portion of this migration. Lifting it back into dmon-core should be a mechanical copy of the `Engine/WizardEngine.cs` shape; the `WizardRenderer.cs` rendering bits collapse into direct dcli dialog calls (no separate renderer file).

The wizard's "Back" affordance gets the `AllowBack=true` flag from `api-ergonomics-pass-1` on the appropriate `SelectRequest`s. Wizard steps that today inject a synthetic "← Back" item at index 0 lose that workaround; the keybinding handles it.

### 4. `MarkdownRenderer.cs`: pure function, no I/O

Today `MarkdownRenderer` produces a Spectre markup `string`. After the port: it produces `IReadOnlyList<Line>` from a parsed Markdig AST. It is a pure transform — no `IConsole`, no `ITerminal` — and unit-tests trivially with markdown-in / line-list-out fixtures.

`MarkdownRenderer` does NOT call `dcli` directly. The renderer's `Line[]` output is consumed by `ConsoleEventHandler` (or wherever assistant-turn rendering lives), which does the `Scrollback.Append` calls.

Why: keep the markdown layer pure. Style derivation (header levels, code blocks, etc.) becomes testable in isolation; rendering composition is dmon's call.

### 5. `InputReader` becomes a state layer, not an OS-input layer

Today `Dmon.Terminal.InputReader` polls stdin on its own thread, exposes `ChannelReader<string>`, and tracks history + buffer + IsLocked.

After the port:
- `dcli` owns the OS-level byte source, parser, and `Events.InputSubmitted` / `InputChanged` stream.
- dmon's renamed-or-not `InputReader` subscribes to that stream, tracks `History` (deque of prior submitted strings) + `IsLocked` (whether dmon currently accepts input) + `CurrentBuffer` (mirrored from `InputChanged`).
- "Locked" state is surfaced as a `Status.SetRows` indicator (e.g. status text changes to "Working…" while a turn is in flight) since `dcli.Input.ReadOnly` is deferred. Locked input is enforced in dmon's `InputSubmitted` handler: if `IsLocked && submitted`, the event is dropped and dmon may emit a "still working" status flash.

Why not extend `dcli`: `Input.ReadOnly` is on the dcli deferred list; pushing it into pass-1 would expand `api-ergonomics-pass-1`'s scope beyond the three blockers. dmon can carry the locked-state semantics itself today and migrate to a first-class `Input.ReadOnly` when dcli adds it.

### 6. Single overlay invariant — adapt dmon's dialog flow

`dcli`'s invariant: **at most one overlay active at a time** (Dialog OR Autocomplete OR Input dialog). dmon today can stack a tool-confirm on top of a wizard step on top of a streaming response. After the port, the same semantic surface is achieved by:
- Streaming response → `Scrollback.BeginLive()` (not an overlay).
- Wizard step → `await SelectAsync` (overlay).
- Tool confirm → `await ChoiceAsync` (overlay).

Stacking a tool-confirm inside a wizard step is not currently possible in dmon's flow (wizards run between turns; confirms run within turns), so the invariant is naturally satisfied. Any future "show a confirm while a wizard is up" requirement would be a dcli feature (overlay stacking), not a dmon workaround.

### 7. CI sees both repos

This change consumes `dcli` as a NuGet package once `0.2.0-rc.x` is published. During development, a local `<ProjectReference>` to `/Users/emmz/github/emmz/dcli/src/Dcli` (and `Dcli.Testing`) is used. The csproj's `<PackageReference>` is the post-merge form; a local-dev-only profile (or just temporary uncommitted refs) handles the gap.

CI must not depend on a local checkout of dcli — once the migration's PR opens, `dcli 0.2.0-rc.x` must be on a feed CI can reach (nuget.org or a GitHub Packages feed). Held until that's true.

## Risks / Trade-offs

- **The `Input.ReadOnly` workaround is dmon-side state** — if dmon's `IsLocked` flag drifts out of sync with what the user sees, locked-input enforcement breaks. *Mitigation:* drive `IsLocked` strictly from RPC events (Turn started / Turn ended), the same source the status line reads from. Add a tier-A test that asserts `InputSubmitted` during `IsLocked=true` does NOT advance dmon's state.
- **`dcli 0.2.0-rc.x` must ship before this migration's final phase** — Phase 5 (drop Spectre) cannot land until `Line.FromText` is in the dcli package. *Mitigation:* the phases are independently mergeable; phases 1–4 can land on a `0.1.0-rc.1` dcli (with the verbose `LineBuilder` ceremony at MarkdownRenderer call sites) and phase 5 lands after `api-ergonomics-pass-1` releases.
- **No nested overlays today, but dmon might want them later** — e.g. a tool-confirm while a wizard is up. *Mitigation:* not in scope; flag as a dcli future feature (overlay stacking).
- **Tier-A fake drift** — if dmon's hand-rolled `ITerminal` fake doesn't track changes to the dcli interface, tests pass locally but the real integration breaks. *Mitigation:* dmon's fake is small (<200 LOC); when `ITerminal` gains a member, the fake fails to compile and the test author has to address it. Optionally, dmon could depend on `Dcli.Testing` for `HeadlessTerminal` and skip the hand-rolled fake — but then every controller test pays the cost of spinning the real loop. Hybrid: tier-A fake for fast unit tests, tier-B `HeadlessTerminal` for one integration test per controller.
- **`Spectre.Console`-via-transitive-dep** — if any dmon-core dependency (outside `Dmon.Terminal`) pulls Spectre transitively, the package stays in the lock file. *Mitigation:* check `dotnet list package --include-transitive` after Phase 5; if a stray Spectre ref remains, audit it.
- **Visual regression on first deploy** — even though "no behaviour change visible to the user" is the stated goal, the substrate change is real (dcli's inline-render model is different from Spectre's direct-write). *Mitigation:* manual run-through against the existing screenshots / demo scripts before each phase merges; the demo runs in `dcli/samples/Dcli.Demo.DmonWizard/` are the reference.

## Migration Plan

The five phases above. Each lands as one section of `tasks.md` (sections §1–§5), one commit, gated by the standard four gates (build / test / format / openspec validate) and reviewer audit. The branch is `change/dmon-migration` off `main` of dmon-core.

Pre-flight requirements:
- `dcli 0.2.0-rc.x` published (or local project refs in place for phases 1–4; package ref required by phase 5).
- A clean working tree on `main`.
- `Dmon.Terminal.csproj` ready to accept the new package refs.

Rollback: each phase is a single commit. Phase N's rollback is `git revert <phase-N-commit>`. The earlier phases stay in place — they're net wins regardless (testability, less Spectre coupling) — and the migration resumes from N+1 once the issue is addressed.

## Open Questions

None blocking. Two known unknowns to revisit per-phase:
1. **Exact mapping of dmon's status-line content to `Status.SetRows`.** dmon today writes status via direct ANSI sequences in `TerminalRenderer.cs`; dcli's `Status` surface takes a list of `Line`s. The shape is the same; the exact row composition is determined in Phase 1 implementation.
2. **Wizard step-by-step `AllowBack` mapping.** Which specific wizard steps want `AllowBack=true` (provider picker, model picker, tool-confirm) is a Phase 2 implementation call, not an architectural decision. Default to `AllowBack=true` on multi-step wizard pickers; `false` on single-step modal prompts.
