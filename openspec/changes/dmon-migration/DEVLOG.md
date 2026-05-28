# DEVLOG — `dmon-migration`

> **Status: in-flight.** Maintained while applying the change per the OpenSpec apply
> workflow (see `CLAUDE.md` § "OpenSpec workflow"). Captures per-section narrative the
> spec files don't carry: decisions under uncertainty, deviations, surfaced bugs, HITL
> verifications. On archive this file moves with the change to
> `openspec/changes/archive/YYYY-MM-DD-dmon-migration/DEVLOG.md` and the status flips
> to **shipped** (see `/devlog freeze`).

## How to resume

- Branch: **`change/dmon-migration`** (created from `main`). Stay on it.
- Working tree state: **CLEAN** (Phase 1 committed at `6d81191`).
- Sanity check command:
  `make build && make test && openspec validate dmon-migration --strict`
- Resume point: **§2 — Port dialog surfaces: `InlinePrompt` / `ToolConfirmPrompt` / `WizardEngine`** (first unticked: `2.1`).
- The fake-`ITerminal` test substrate (`test/Dmon.Terminal.Tests/Fakes/`) is in place and reviewer-signed-off; Phase 2's WizardEngine tests should consume it directly via scripted `OnSelectAsync` / `OnChoiceAsync` / `OnInputAsync` handlers.
- The local `<ProjectReference>` to `/Users/emmz/github/emmz/dcli/src/Dcli/Dcli.csproj` stays during development; swap to `<PackageReference Version="0.2.0-rc.x" />` before the migration PR opens.
- Check the memory files listed at the bottom before briefing — they encode hard-won constraints across this and prior changes.

## Section status

| § | Section | Commit | Tests after | Notes |
|---|---------|--------|-------------|-------|
| 1 | Wire up dcli; port TerminalRenderer | `6d81191` | 41 in `Dmon.Terminal.Tests` (17 Fakes + 10 WizardEngine + 13 new tier-A + 1 tier-B) | Chunked across 3 worker calls (substrate, substrate fixes, renderer port). One reviewer-loop iteration on the substrate (empty-string `AppendText` drift fix); renderer port passed first review. `SettleTurn` styled re-render explicitly deferred to Phase 5. §1.6 smoke reframed to a limited scope — see Decisions. |

## Decisions & deviations

### §1 — `SettleTurn` styled re-render deferred to Phase 5

Today's `SettleTurn(string spectreMarkup)` calls `AnsiConsole.MarkupLine` on Spectre markup produced by `MarkdownRenderer`. The natural dcli mapping is `liveBlock.SetContent(IReadOnlyList<Line>)` *before* `Commit` — but `Line[]` output from `MarkdownRenderer` doesn't exist until Phase 5 (§5.2). Phase 1's `SettleTurn` keeps its `string` signature for call-site compatibility and ignores the argument; the committed live block (raw streamed tokens) IS the final assistant turn for Phase 1. Phase 5 will populate the live block via `SetContent(Line[])` before `Commit` to restore styled rendering. Decided up front in the orchestrator brief rather than improvised by the worker.

### §1 — Tier-A vs tier-B test split: lean harder on `HeadlessTerminal` than design suggested

`design.md` Decision 2 originally framed the tier-A hand-rolled fake as the primary substrate. During the testability-story discussion before Phase 1 the orchestrator and user agreed to **lean harder on `Dcli.Testing.HeadlessTerminal`** for any test where the dmon→dcli seam is itself under test (wizard back-stack, InputReader state-layer integration); the hand-rolled `FakeTerminal` stays for argument-level call-sequence assertions in the renderer and (future) event-handler. Rationale: a hand-rolled fake re-implements parts of dcli's contract (overlay exclusivity, `BeginLive`/`Commit` lifecycle) and can silently drift from the real semantics. Mitigations baked into the fake: enforce no-op-after-Commit, no-op-after-SetContent, no-op-on-empty-AppendText (mirrors `LiveBlock.cs:39`); throw `NotImplementedException` for unmodelled surfaces (`Autocomplete`, `MultiSelect`, `BeginCollapsible`); record `Opened` calls *before* the scripted handler runs so cancellation tests still see the request.

### §1 — `ConsoleEventHandler.HandleAsync(TerminalEvent)` will be public in Phase 3

Settled during the FakeTerminal design discussion: when Phase 3 refactors `ConsoleEventHandler` into a thin dcli adapter, it will expose `public Task HandleAsync(TerminalEvent ev, CancellationToken ct)` as the test seam, with a thin `DrainAsync(ChannelReader)` wrapper for production. Tests then call `HandleAsync` directly — deterministic, no "wait for the consumer to drain" timing. Captured here so Phase 3's worker brief inherits the constraint.

### §1.6 — Phase 1 manual smoke reframed (input path is Phase 4)

`tasks.md` §1.6 originally read "run the full app, send a turn, observe streaming + separator + status all render correctly on dcli". On running `build/dmon`, the user observed the dcli fixed region rendered correctly but Enter cleared the input editor with no further effect — no message reached the core, slash commands didn't fire.

**Root cause:** `dcli.Terminal.StartAsync` puts the terminal in raw mode and routes all stdin into dcli's VT parser → `Events.InputSubmitted`. The legacy `InputReader` still polls stdin on its own thread (dcli wins the raw-mode contest, `InputReader` starves), and **nothing in Phase 1 reads `Events.InputSubmitted`** — that wiring is Phase 4 (`InputReader` state layer) + Phase 3 (dispatch). Phase 1's full-app smoke was over-specified.

**Decision:** §1.6 reframed to a limited scope smoke ("app launches, dcli fixed region renders, Ctrl+C exits cleanly"). The renderer port is verified by the tier-A and tier-B tests in §1.4/§1.5 until the input path is wired. The same blocker will hit §2.8 ("provider-setup wizard end-to-end") and §3.6 ("hit every RPC event type") — both will be reframed identically when those sections land. Rejected alternatives: (B) adding an interim input bridge in Phase 1, (C) reordering Phases — both risk turning the temporary into the load-bearing.

## Human-in-the-loop verifications

- **§1.6 — Phase 1 limited smoke.**
  - Command: `make build && build/dmon` then Ctrl+C.
  - Expected: app launches without crash, dcli fixed region renders (status row + input editor visible), Ctrl+C exits cleanly.
  - Status: **done** (`2026-05-28`).
- **§2.8 — Phase 2 limited smoke** (when Phase 2 lands): app continues to launch cleanly; new dialog code paths are exercised by tier-A tests; full end-to-end wizard run is deferred until Phase 4.
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

## Resume point

> **Currently at §2 — Port dialog surfaces (first unticked task: `2.1`).** Phase 1 committed at `6d81191`. The fake substrate at `test/Dmon.Terminal.Tests/Fakes/` is signed off and ready for §2's wizard / tool-confirm / input-request tests. Next worker call: delete `WizardRenderer.cs` and `InlinePrompt.cs`, port `WizardEngine.cs` (drop the `Func<WizardStep, Outcome>` quarantine seam, call `await terminal.SelectAsync` / `InputAsync` directly, preserve back-stack byte-for-byte), port `ToolConfirmPrompt.cs` to `await terminal.ChoiceAsync`. Reference: `/Users/emmz/github/emmz/dcli/samples/Dcli.Demo.DmonWizard/Engine/WizardEngine.cs`.
