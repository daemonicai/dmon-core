# DEVLOG — `dmon-migration`

> **Status: in-flight.** Maintained while applying the change per the OpenSpec apply
> workflow (see `CLAUDE.md` § "OpenSpec workflow"). Captures per-section narrative the
> spec files don't carry: decisions under uncertainty, deviations, surfaced bugs, HITL
> verifications. On archive this file moves with the change to
> `openspec/changes/archive/YYYY-MM-DD-dmon-migration/DEVLOG.md` and the status flips
> to **shipped** (see `/devlog freeze`).

## How to resume

- Branch: **`change/dmon-migration`** (created from `main`). Stay on it.
- Working tree state: **CLEAN** (Phase 2 committed).
- Sanity check command:
  `make build && make test && openspec validate dmon-migration --strict`
- Resume point: **§3 — `ConsoleEventHandler` adapter refactor** (first unticked: `3.1`).
- The fake-`ITerminal` test substrate (`test/Dmon.Terminal.Tests/Fakes/`) is in place and reviewer-signed-off; Phase 2's WizardEngine tests consume it directly via scripted `OnSelectAsync` / `OnInputAsync` handlers; Phase 3's `ConsoleEventHandler` tests get the new `HandleAsync(TerminalEvent)` seam (see Decisions §1).
- The local `<ProjectReference>` to `/Users/emmz/github/emmz/dcli/src/Dcli/Dcli.csproj` stays during development; dcli is now at `0.2.0-rc.2` (multi-line-dialog-prompts shipped); swap to `<PackageReference>` before the migration PR opens.
- Check the memory files listed at the bottom before briefing — they encode hard-won constraints across this and prior changes.

## Section status

| § | Section | Commit | Tests after | Notes |
|---|---------|--------|-------------|-------|
| 1 | Wire up dcli; port TerminalRenderer | `6d81191` | 41 in `Dmon.Terminal.Tests` (17 Fakes + 10 WizardEngine + 13 new tier-A + 1 tier-B) | Chunked across 3 worker calls (substrate, substrate fixes, renderer port). One reviewer-loop iteration on the substrate (empty-string `AppendText` drift fix); renderer port passed first review. `SettleTurn` styled re-render explicitly deferred to Phase 5. §1.6 smoke reframed to a limited scope — see Decisions. |
| 2 | Port dialog surfaces | (this commit) | 58 in `Dmon.Terminal.Tests` (+17 net since Phase 1: rewritten `WizardEngineTests` + new `ToolConfirmPromptTests`) | Chunked across 2 worker calls (initial port + workaround removal once dcli rc.2 landed). The initial port shipped a scrollback workaround in `ToolConfirmPrompt` because dcli's `ChoiceRequest.Prompt` was single-line; user drove a separate dcli `multi-line-dialog-prompts` change (now archived) and the second worker call dropped the workaround. `WizardRenderer` + `InlinePrompt` deleted; `WizardEngine` ports with back-stack byte-for-byte from the dcli reference; `ConsoleEventHandler` got the minimal shape-preserving updates (+19 lines: `ITerminal` field, ctor param, three call-site updates) — Phase 3 will refactor it into a thin adapter. `UiInputRequestTests` deferred to Phase 3 because `ConsoleEventHandler` has no clean test seam yet. §2.8 smoke remains a limited scope per the §1.6 decision. |

## Decisions & deviations

### §1 — `SettleTurn` styled re-render deferred to Phase 5

Today's `SettleTurn(string spectreMarkup)` calls `AnsiConsole.MarkupLine` on Spectre markup produced by `MarkdownRenderer`. The natural dcli mapping is `liveBlock.SetContent(IReadOnlyList<Line>)` *before* `Commit` — but `Line[]` output from `MarkdownRenderer` doesn't exist until Phase 5 (§5.2). Phase 1's `SettleTurn` keeps its `string` signature for call-site compatibility and ignores the argument; the committed live block (raw streamed tokens) IS the final assistant turn for Phase 1. Phase 5 will populate the live block via `SetContent(Line[])` before `Commit` to restore styled rendering. Decided up front in the orchestrator brief rather than improvised by the worker.

### §1 — Tier-A vs tier-B test split: lean harder on `HeadlessTerminal` than design suggested

`design.md` Decision 2 originally framed the tier-A hand-rolled fake as the primary substrate. During the testability-story discussion before Phase 1 the orchestrator and user agreed to **lean harder on `Dcli.Testing.HeadlessTerminal`** for any test where the dmon→dcli seam is itself under test (wizard back-stack, InputReader state-layer integration); the hand-rolled `FakeTerminal` stays for argument-level call-sequence assertions in the renderer and (future) event-handler. Rationale: a hand-rolled fake re-implements parts of dcli's contract (overlay exclusivity, `BeginLive`/`Commit` lifecycle) and can silently drift from the real semantics. Mitigations baked into the fake: enforce no-op-after-Commit, no-op-after-SetContent, no-op-on-empty-AppendText (mirrors `LiveBlock.cs:39`); throw `NotImplementedException` for unmodelled surfaces (`Autocomplete`, `MultiSelect`, `BeginCollapsible`); record `Opened` calls *before* the scripted handler runs so cancellation tests still see the request.

### §1 — `ConsoleEventHandler.HandleAsync(TerminalEvent)` will be public in Phase 3

Settled during the FakeTerminal design discussion: when Phase 3 refactors `ConsoleEventHandler` into a thin dcli adapter, it will expose `public Task HandleAsync(TerminalEvent ev, CancellationToken ct)` as the test seam, with a thin `DrainAsync(ChannelReader)` wrapper for production. Tests then call `HandleAsync` directly — deterministic, no "wait for the consumer to drain" timing. Captured here so Phase 3's worker brief inherits the constraint.

### §2 — Resolved: dcli widened `ChoiceRequest.Prompt`, workaround dropped

**Why:** Phase 2's first worker pass surfaced an API gap: dcli's `ChoiceRequest.Prompt` was `Line?` (single-line) while the `terminal-host` spec for tool confirmation specifies the `⚠ HIGH RISK` indicator and tool/args lines live "in the request's prompt content" / "before the option list" (multi-line, inside the overlay).

**How to apply:** the user chose option 2 — fix dcli rather than ship the workaround in dmon, per the principle in `[[feedback-workaround-as-substrate-signal]]`. A separate dcli OpenSpec change `multi-line-dialog-prompts` widened the preamble fields on all four request types (`SelectRequest.Title`, `MultiSelectRequest.Title`, `ChoiceRequest.Prompt`, `InputRequest.Prompt`) from `Line?` to `IReadOnlyList<Line>?` with backwards-compat overload constructors. dcli shipped as `0.2.0-rc.2`.

A follow-up dmon worker call dropped the scrollback workaround in `ToolConfirmPrompt.cs` (58 → 52 LOC) and rewrote `ToolConfirmPromptTests.cs` so assertions target `ChoiceOpened.Request.Prompt` instead of preceding `ScrollbackAppendLine` calls. The new structural test `ShowAsync_LowRisk_NoScrollbackCallsBeforeDialog` is a regression guard against the workaround creeping back in. Spec text now matches the implementation literally. Reviewer signed off no blockers.

**Rejected alternatives:** (1) accept the workaround and tighten the spec at archive — would lock in the dcli gap; (3) update the dmon spec to specify "scrollback above overlay" as canonical — would codify a workaround as canonical behaviour. Option 2 (fix dcli) was the right move.

### §1.6 — Phase 1 manual smoke reframed (input path is Phase 4)

`tasks.md` §1.6 originally read "run the full app, send a turn, observe streaming + separator + status all render correctly on dcli". On running `build/dmon`, the user observed the dcli fixed region rendered correctly but Enter cleared the input editor with no further effect — no message reached the core, slash commands didn't fire.

**Root cause:** `dcli.Terminal.StartAsync` puts the terminal in raw mode and routes all stdin into dcli's VT parser → `Events.InputSubmitted`. The legacy `InputReader` still polls stdin on its own thread (dcli wins the raw-mode contest, `InputReader` starves), and **nothing in Phase 1 reads `Events.InputSubmitted`** — that wiring is Phase 4 (`InputReader` state layer) + Phase 3 (dispatch). Phase 1's full-app smoke was over-specified.

**Decision:** §1.6 reframed to a limited scope smoke ("app launches, dcli fixed region renders, Ctrl+C exits cleanly"). The renderer port is verified by the tier-A and tier-B tests in §1.4/§1.5 until the input path is wired. The same blocker will hit §2.8 ("provider-setup wizard end-to-end") and §3.6 ("hit every RPC event type") — both will be reframed identically when those sections land. Rejected alternatives: (B) adding an interim input bridge in Phase 1, (C) reordering Phases — both risk turning the temporary into the load-bearing.

## Human-in-the-loop verifications

- **§1.6 — Phase 1 limited smoke.**
  - Command: `make build && build/dmon` then Ctrl+C.
  - Expected: app launches without crash, dcli fixed region renders (status row + input editor visible), Ctrl+C exits cleanly.
  - Status: **done** (`2026-05-28`).
- **§2.8 — Phase 2 limited smoke.**
  - Command: `make build && build/dmon` then Ctrl+C.
  - Expected: app launches without crash (no regression from Phase 1); Ctrl+C exits cleanly. Full wizard / tool-confirm / ui.inputRequest smoke remains deferred to Phase 4.
  - Status: **deferred to Phase 4** (input path not yet wired — same blocker as §1.6).
- **§3.6 — Phase 3 limited smoke** (when Phase 3 lands): same caveat as §2.8.

## Open follow-ups / known gaps (after this change lands — NOT in scope here)

- **Local `<ProjectReference>` to dcli must swap to `<PackageReference Version="0.2.0-rc.x" />` before opening the migration PR.** Tracked as a reviewer nit on §1.1.
- **`SettleTurn` parameter name `spectreMarkup`** will be misleading once Phase 5 changes its semantic (now markdown source, not Spectre markup). Rename then; not now (avoids churn).
- **`PrintPrompt` stub + `RefreshStatus()` redundancy on `TurnEndEvent`** — `ConsoleEventHandler.cs:69-70` triggers `Status.SetRows` twice with identical payload. Cosmetic; dcli's frame loop collapses them. Phase 3 deletes both the stub and the call sites.
- **`Console.CancelKeyPress` handler in `Program.cs:65-69` will collide with dcli's Ctrl+C semantics** unless Phase 3 (§3.4) treats it as a redundancy net. Tracked for Phase 3 brief.
- **Turn-history persistence across `/reload`** — out of scope for this change; needs a separate core turn-persistence change. Memory file: `[[followup-turn-persistence-across-restart]]`.

## Memory files (indexed by `~/.claude/projects/-Users-emmz-github-emmz-dmon-core/memory/MEMORY.md`)

- `opsx-archive-sync-manual` — `openspec archive <slug> -y` syncs specs and moves the change; aborts on malformed standing specs (fix `## Requirements` / `## Purpose` headers first).
- `dmon-tui-dead-end` — `Dmon.Tui` is abandoned; the live terminal host is `Dmon.Terminal`. All Phase work in this change is in `src/Dmon.Terminal/`.
- `followup-turn-persistence-across-restart` — conversation history is lost on `/reload`; separate core turn-persistence change needed.
- `feedback-workaround-as-substrate-signal` — workarounds in dmon for dcli API gaps are signals to fix dcli; the migration is a vehicle for proving and improving dcli.

## Resume point

> **Currently at §3 — `ConsoleEventHandler` adapter refactor (first unticked task: `3.1`).** Phase 2 committed. The fake substrate + `HandleAsync(TerminalEvent)` test-seam pattern (settled in Phase 1 design discussion, see Decisions above) is the substrate for §3. Next worker call: delete `AddProviderCommand.cs` and `ReloadCommand.cs` (§3.1); refactor `ConsoleEventHandler` into a thin adapter (§3.2) — RPC events → dcli calls, no direct console writes; expose `public Task HandleAsync(TerminalEvent ev, CancellationToken ct)` with a thin `DrainAsync(ChannelReader)` wrapper; wire `Events.InputSubmitted` (§3.3 — slash command parsing local, forward to core via existing RPC); wire `KeyPressed(Ctrl+C)` to graceful shutdown (§3.4 — the existing `Console.CancelKeyPress` handler stays as a redundancy net per `[[feedback-workaround-as-substrate-signal]]`-adjacent reasoning about the unwiring being explicit). `ConsolePicker` call sites in `ConsoleEventHandler.cs:256, 291` (provider/model picker for `/model`) should migrate to `SelectAsync` calls during this refactor. The `UiInputRequestTests` deferred from Phase 2 land here.
