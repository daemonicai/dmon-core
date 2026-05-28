# DEVLOG — `dmon-migration`

> **Status: in-flight.** Maintained while applying the change per the OpenSpec apply
> workflow (see `CLAUDE.md` § "OpenSpec workflow"). Captures per-section narrative the
> spec files don't carry: decisions under uncertainty, deviations, surfaced bugs, HITL
> verifications. On archive this file moves with the change to
> `openspec/changes/archive/YYYY-MM-DD-dmon-migration/DEVLOG.md` and the status flips
> to **shipped** (see `/devlog freeze`).

## How to resume

- Branch: **`change/dmon-migration`** (created from `main`). Stay on it.
- Working tree state: **CLEAN** (Phase 4 committed; §4.5 manual smoke pending user verification — see HITL section below).
- Sanity check command:
  `make build && make test && openspec validate dmon-migration --strict`
- Resume point: **§5 — `MarkdownRenderer` rewrite + drop Spectre** (first unticked: `5.1`).
- The fake-`ITerminal` test substrate (`test/Dmon.Terminal.Tests/Fakes/`) is in place and reviewer-signed-off; Phase 2's WizardEngine tests consume it directly via scripted `OnSelectAsync` / `OnInputAsync` handlers; Phase 3's `ConsoleEventHandler` tests get the new `HandleAsync(TerminalEvent)` seam (see Decisions §1).
- The local `<ProjectReference>` to `/Users/emmz/github/emmz/dcli/src/Dcli/Dcli.csproj` stays during development; dcli is now at `0.2.0-rc.2` (multi-line-dialog-prompts shipped); swap to `<PackageReference>` before the migration PR opens.
- Check the memory files listed at the bottom before briefing — they encode hard-won constraints across this and prior changes.

## Section status

| § | Section | Commit | Tests after | Notes |
|---|---------|--------|-------------|-------|
| 1 | Wire up dcli; port TerminalRenderer | `6d81191` | 41 in `Dmon.Terminal.Tests` (17 Fakes + 10 WizardEngine + 13 new tier-A + 1 tier-B) | Chunked across 3 worker calls (substrate, substrate fixes, renderer port). One reviewer-loop iteration on the substrate (empty-string `AppendText` drift fix); renderer port passed first review. `SettleTurn` styled re-render explicitly deferred to Phase 5. §1.6 smoke reframed to a limited scope — see Decisions. |
| 2 | Port dialog surfaces | `fccf86b` | 58 in `Dmon.Terminal.Tests` (+17 net since Phase 1: rewritten `WizardEngineTests` + new `ToolConfirmPromptTests`) | Chunked across 2 worker calls (initial port + workaround removal once dcli rc.2 landed). The initial port shipped a scrollback workaround in `ToolConfirmPrompt` because dcli's `ChoiceRequest.Prompt` was single-line; user drove a separate dcli `multi-line-dialog-prompts` change (now archived) and the second worker call dropped the workaround. `WizardRenderer` + `InlinePrompt` deleted; `WizardEngine` ports with back-stack byte-for-byte from the dcli reference; `ConsoleEventHandler` got the minimal shape-preserving updates (+19 lines: `ITerminal` field, ctor param, three call-site updates) — Phase 3 will refactor it into a thin adapter. `UiInputRequestTests` deferred to Phase 3 because `ConsoleEventHandler` has no clean test seam yet. §2.8 smoke remains a limited scope per the §1.6 decision. |
| 3 | Adapter: `ConsoleEventHandler` | `0dc843f` | 80 in `Dmon.Terminal.Tests` (+22 net since Phase 2: new `ConsoleEventHandlerTests` covering the `HandleAsync(TerminalEvent)` seam, Ctrl+C detection ±negative cases, picker `SelectAsync` migration, deferred-from-Phase-2 `UiInputRequest`, RPC-dispatch smoke, `DrainAsync` happy path) | Chunked across 3 worker calls (source refactor + `PrintPrompt` cleanup + tier-A tests). Reviewer approved first audit with 0 blockers + 6 nits; addressed nits 1/3/4 (parameter naming symmetry, no-side-effect snapshots on KeyPressed negative tests, Ctrl+C-while-locked spec scenario test); nits 2/5/6 deferred (see Decisions §3). `AddProviderCommand` / `ReloadCommand` marker records replaced with `SlashCommandParser.ClientCommandKind` enum; `ConsolePicker.cs` deleted (both picker call sites migrated to `_terminal.SelectAsync` with `AllowBack=true`); `PrintPrompt` stub + redundant `RefreshStatus` call after `TurnEndEvent`/`AgentReadyEvent` removed. §3.6 smoke remains limited-scope per the §1.6 decision; full end-to-end smoke unblocks in Phase 4. |
| 4 | State layer: `InputReader` | (this commit) | 96 in `Dmon.Terminal.Tests` (+16 net since Phase 3: new `InputStateLayerTests` 13 covering buffer mirror under lock, history append/drop/whitespace/bounded/eviction-while-locked, IsLocked default+roundtrip; 3 new `ConsoleEventHandlerTests` covering the dispatch-layer locked-drop + unlock re-enables forwarding) | Chunked across 2 worker calls (source + tests). Reviewer approved first audit with 0 blockers + 5 cosmetic nits + 4 architectural notes — no nit fixes applied (the duplicate-lock-check WHY-comment requested by nit 3 was already present in the worker's first pass). `InputReader.cs` renamed `InputStateLayer.cs` (the type is a pure state mirror, not a reader); `Program.cs` lost ~30 lines of `inputTask` / `inputEnum` plumbing (`Task.WhenAny(nextEvent, reloadSignal.Task)` replaces the dual-source `WhenAny`); worker added a `TaskCompletionSource<bool> reloadSignal` deviation that the brief was missing — without it `/reload` would silently hang since input no longer flows through the session loop. §4.5 manual smoke pending user verification — it's the unblock point for the deferred §1.6 / §2.8 / §3.6 end-to-end smokes. |

## Decisions & deviations

### §1 — `SettleTurn` styled re-render deferred to Phase 5

Today's `SettleTurn(string spectreMarkup)` calls `AnsiConsole.MarkupLine` on Spectre markup produced by `MarkdownRenderer`. The natural dcli mapping is `liveBlock.SetContent(IReadOnlyList<Line>)` *before* `Commit` — but `Line[]` output from `MarkdownRenderer` doesn't exist until Phase 5 (§5.2). Phase 1's `SettleTurn` keeps its `string` signature for call-site compatibility and ignores the argument; the committed live block (raw streamed tokens) IS the final assistant turn for Phase 1. Phase 5 will populate the live block via `SetContent(Line[])` before `Commit` to restore styled rendering. Decided up front in the orchestrator brief rather than improvised by the worker.

### §1 — Tier-A vs tier-B test split: lean harder on `HeadlessTerminal` than design suggested

`design.md` Decision 2 originally framed the tier-A hand-rolled fake as the primary substrate. During the testability-story discussion before Phase 1 the orchestrator and user agreed to **lean harder on `Dcli.Testing.HeadlessTerminal`** for any test where the dmon→dcli seam is itself under test (wizard back-stack, InputReader state-layer integration); the hand-rolled `FakeTerminal` stays for argument-level call-sequence assertions in the renderer and (future) event-handler. Rationale: a hand-rolled fake re-implements parts of dcli's contract (overlay exclusivity, `BeginLive`/`Commit` lifecycle) and can silently drift from the real semantics. Mitigations baked into the fake: enforce no-op-after-Commit, no-op-after-SetContent, no-op-on-empty-AppendText (mirrors `LiveBlock.cs:39`); throw `NotImplementedException` for unmodelled surfaces (`Autocomplete`, `MultiSelect`, `BeginCollapsible`); record `Opened` calls *before* the scripted handler runs so cancellation tests still see the request.

### §1 — `ConsoleEventHandler.HandleAsync(TerminalEvent)` will be public in Phase 3

Settled during the FakeTerminal design discussion: when Phase 3 refactors `ConsoleEventHandler` into a thin dcli adapter, it will expose `public Task HandleAsync(TerminalEvent ev, CancellationToken ct)` as the test seam, with a thin `DrainAsync(ChannelReader)` wrapper for production. Tests then call `HandleAsync` directly — deterministic, no "wait for the consumer to drain" timing. Captured here so Phase 3's worker brief inherits the constraint.

### §4 — `reloadSignal` TCS deviation (worker-initiated, accepted)

The Phase 4 source-refactor brief described the `Program.cs` simplification as "the whole `Task.WhenAny(nextInputLine, nextEvent)` collapses to just awaiting `nextEvent` — input now flows through `DrainAsync` independently and only events come from the core process here." This was incomplete: the old loop also relied on `reloadRequested` being checked synchronously when input handling returned, and after Phase 3 input handling moved into `DrainAsync` (a separate task). With input flowing on a different task than the session loop, setting `reloadRequested = true` in the lambda no longer unblocks the loop — the next iteration would only fire when the core happened to emit an RPC event.

The worker spotted the gap and replaced the bool with a `TaskCompletionSource<bool> reloadSignal`. The lambda passed to `ConsoleEventHandler` now `TrySetResult(true)`s the signal, and the session loop awaits `Task.WhenAny(nextEvent, reloadSignal.Task)`. The signal is re-created at the top of each `RunSessionAsync` iteration so it doesn't latch between sessions. **Accepted as the correct fix for a brief-level oversight** rather than rejected as out-of-scope creativity.

Reviewer flagged a subtle ordering observation (Architectural note 1 below): if the user mashes `/reload` during the in-between restart window, the second `_requestReload()` fires `TrySetResult(true)` on the *already-new* `reloadSignal`, causing a benign double-restart. Tracked in Open follow-ups; not blocking Phase 4 since it's benign-but-noisy rather than a correctness bug.

### §4 — Reviewer architectural notes (recorded, not addressed in this phase)

Four architectural notes from the Phase 4 reviewer audit, all explicitly non-blocking:

1. **`reloadSignal` rapid-fire double-restart** — described above; tracked in Open follow-ups.
2. **`InputStateLayer.IsLocked` thread-safety.** The previous `InputReader._isLocked` was `volatile bool` to bridge the polling thread and the RPC dispatch thread. In the new model, `IsLocked` is set in `HandleAsync(Event)` (session-loop task) and read in `HandleAsync(TerminalEvent)` (`DrainAsync` task). Both run on the same async context's thread pool but with no formal same-thread guarantee; reordering is permitted. Worst case: the dispatch layer briefly sees a stale value and forwards/drops one extra submission across a `TurnStart`/`TurnEnd` boundary. **Cheap fix if it ever bites:** mark `IsLocked` as `volatile bool` (matches the old contract) or guard transitions with a memory barrier. Skipped this phase because the symptom is invisible to the user and the new model removes the tight polling loop that made volatility load-bearing.
3. **`InputStateLayer.History` is dead code from production POV.** The state layer preserves history per `design.md §5` ("History (deque of prior submitted strings)") but no UI consumes it today — Up/Down recall is dcli's responsibility (and likely deferred there too). Either wire it to a dmon-side recall UI later, push it into dcli's editor on subscribe, or drop it. Phase 4 preserves it per `tasks.md §4.2`; revisit on Phase 5 entry.
4. **`Modifiers.None` Ctrl-C tolerance** — pre-existing from Phase 3, noted by the reviewer but explicitly out-of-scope for this audit.

### §3 — Reviewer nits 2, 5, 6 deferred (with rationale)

After the Phase 3 group passed the reviewer audit (verdict APPROVE, 0 blockers, 6 nits), three nits were explicitly **not** addressed in this commit:

- **Nit 2 — strict vs. tolerant Ctrl+C modifier check.** Implementation uses `(Modifiers & Ctrl) != None`, which accepts `Ctrl|Alt+C`. The spec text says only `KeyEvent(Char('c'), Modifiers.Ctrl)`. Per dcli's `Modifiers` doc-comment, Shift is never reported with Ctrl, so the only realistic combo is Ctrl+Alt+C — which a user typing for shutdown intent should still see honoured. Defensible; no change.
- **Nit 5 — `AgentReadyEvent` no longer self-refreshes status.** The deleted `PrintPrompt()` call after `[Ready]` used to invoke `RefreshStatus()`. At that point `_modelName` is empty so the refresh wrote an empty row anyway, and dcli's frame loop keeps the fixed region painted independently. No behaviour regression; nothing to fix.
- **Nit 6 — picker pre-selection UX lost.** `RunProviderPickerAsync` / `RunModelPickerAsync` previously computed a `preSelect` index and passed it to `ConsolePicker.Run` so the cursor opened on the active provider/model. dcli's `SelectRequest` has no `InitialIndex` / `PreSelect` field today. Spec doesn't require it; matches `WizardEngine`'s `SelectAsync` calls which also don't pre-select. **Tracked as a follow-up** for either a dcli ergonomics pass or a dmon-side restore once dcli adds the surface — see Open follow-ups below.

Why deferred rather than fixed: per `feedback-workaround-as-substrate-signal` reasoning, nit 6 wants a dcli API addition rather than a dmon workaround; nit 2 codifies a spec edge-case that doesn't yet matter; nit 5 is a documentation-only artefact of the deletion. Capturing them here ensures the next reviewer round (or future-me) can see why each was left in place.

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
- **§3.6 — Phase 3 limited smoke.**
  - Command: `make build && build/dmon` then Ctrl+C.
  - Expected: app launches without crash; legacy `Console.CancelKeyPress` net still works; new dcli `KeyPressed(Ctrl+C)` path is exercisable by tier-A tests. Full RPC dispatch smoke (input → turn → streaming → tool-confirm → wizard) remains deferred to Phase 4 — the 80 tier-A tests in `Dmon.Terminal.Tests` (including the new `ConsoleEventHandlerTests`) cover the adapter's wiring.
  - Status: **deferred to Phase 4** (input path not yet wired — same blocker as §1.6 and §2.8).
- **§4.5 — Phase 4 full end-to-end smoke** (unblocks §1.6, §2.8, §3.6 retroactively).
  - Command: `make build && build/dmon`.
  - Recipe (user runs interactively — automated gates can't observe live terminal rendering):
    1. Type a few characters. They should appear in the dcli input editor at the bottom of the screen.
    2. Press Enter on a plain message (no slash). Confirm: the message shows above as `❯ <text>`, a "thinking…" status appears in the top status row, the assistant streams tokens into the scrollback, and the turn settles cleanly.
    3. Type a message DURING streaming. Confirm: characters echo in the editor; pressing Enter while the turn is still in-flight **does not** submit anything (no double-message; the in-flight stream continues to settle as normal).
    4. After the turn settles, press Enter on whatever's still in the buffer. Confirm: the submission is accepted (lock released).
    5. Type `/reload` and press Enter. Confirm: `[Reload] Restarting core…` appears, the core restarts, and `[Reload] Core restarted.` follows. A new turn after restart works normally.
    6. Press Ctrl+C. Confirm: the app exits cleanly (no stack trace, no hung process).
  - Status: **pending user verification.** Tick §4.5 in `tasks.md` after the user signs off.

## Open follow-ups / known gaps (after this change lands — NOT in scope here)

- **`/reload` rapid-fire double-restart window.** Phase 4's `reloadSignal` TCS is re-created at the top of each `RunSessionAsync` iteration *before* `CoreProcess.RestartAsync()` returns. If the user mashes `/reload` during the restart window, the second `_requestReload()` lights up the already-new signal and the next iteration immediately requests a second restart. Benign-but-noisy. If real-world `/reload` mashing causes UX weirdness, move signal recreation to *after* `RestartAsync` returns. Tracked from Phase 4 reviewer audit (Architectural note 1).
- **`InputStateLayer.IsLocked` not declared `volatile`** — the previous `InputReader._isLocked` was volatile because the polling thread read it while the dispatch thread wrote it. In the new model both touch points live on async tasks (no formal same-thread guarantee); reordering is permitted. Worst case is one extra submission forwarded/dropped across a `TurnStart`/`TurnEnd` boundary. Cheap fix if ever observed: `volatile bool` or a method that issues a memory barrier. Tracked from Phase 4 reviewer audit (Architectural note 2).
- **`InputStateLayer.History` is currently dead from production's POV** — preserved per `design.md §5` and `tasks.md §4.2` but no UI consumes it. Decide on Phase 5 entry: wire to a dmon recall UI / push to dcli's editor on subscribe / drop. Tracked from Phase 4 reviewer audit (Architectural note 3).
- **Local `<ProjectReference>` to dcli must swap to `<PackageReference Version="0.2.0-rc.x" />` before opening the migration PR.** Tracked as a reviewer nit on §1.1.
- **`SettleTurn` parameter name `spectreMarkup`** will be misleading once Phase 5 changes its semantic (now markdown source, not Spectre markup). Rename then; not now (avoids churn).
- **Picker pre-selection regressed.** Phase 3's `RunProviderPickerAsync` / `RunModelPickerAsync` migrated from `ConsolePicker.Run(items, preSelect)` to `_terminal.SelectAsync(SelectRequest(..., AllowBack: true))`. dcli's `SelectRequest` has no `InitialIndex` field today, so the `/model` picker now opens with the cursor at the top rather than on the active provider/model. Spec doesn't require pre-selection; matches the wizard's pickers; not user-blocking. Restore when dcli adds the surface (likely a future ergonomics pass).
- **`DrainAsync` only swallows `OperationCanceledException`.** Other exceptions from `ReadAllAsync` (e.g. if dcli ever calls `Writer.Complete(exception)`) propagate out and fault `Task.WhenAll` in `Program.cs`. dcli does not signal channel faults today, so the surface is dormant. Hardening note for a follow-up.
- **`HandleAsync` overload pair** — `ConsoleEventHandler` now has `HandleAsync(Event @event, ...)` for RPC inbound and `HandleAsync(TerminalEvent @event, ...)` for UI inbound. Overload resolution disambiguates cleanly but a reader new to the file has to look twice. Rename to `HandleRpcEventAsync` / `HandleUiEventAsync` if Phase 4 consolidates the adapter surface.
- **Turn-history persistence across `/reload`** — out of scope for this change; needs a separate core turn-persistence change. Memory file: `[[followup-turn-persistence-across-restart]]`.

## Memory files (indexed by `~/.claude/projects/-Users-emmz-github-emmz-dmon-core/memory/MEMORY.md`)

- `opsx-archive-sync-manual` — `openspec archive <slug> -y` syncs specs and moves the change; aborts on malformed standing specs (fix `## Requirements` / `## Purpose` headers first).
- `dmon-tui-dead-end` — `Dmon.Tui` is abandoned; the live terminal host is `Dmon.Terminal`. All Phase work in this change is in `src/Dmon.Terminal/`.
- `followup-turn-persistence-across-restart` — conversation history is lost on `/reload`; separate core turn-persistence change needed.
- `feedback-workaround-as-substrate-signal` — workarounds in dmon for dcli API gaps are signals to fix dcli; the migration is a vehicle for proving and improving dcli.

## Resume point

> **Currently at §5 — `MarkdownRenderer` rewrite + drop `Spectre.Console` (first unticked task: `5.1`).** Phase 4 committed (§4.5 smoke pending user verification — see HITL section). The legacy OS-input thread is gone; dcli owns input fully; `InputStateLayer` mirrors `CurrentBuffer` from `InputChanged` and tracks bounded `History` from `InputSubmitted`; locked-drop is enforced in both the state layer (no History) and the dispatch layer (no core forward). Phase 5 is the last phase: rewrite `MarkdownRenderer` to emit `IReadOnlyList<Line>` (not Spectre markup `string`), wire `SettleTurn` to use `liveBlock.SetContent(Line[])` before `Commit` to restore styled rendering, remove the `Spectre.Console` package reference, confirm no transitive Spectre dep remains, and archive the change. **Prerequisite:** `dcli 0.2.0-rc.x` available — currently consumed via local `<ProjectReference>`, which must swap to `<PackageReference>` before the migration PR opens (tracked as a Phase 1 nit). Also revisit Architectural note 3 from Phase 4: decide whether `InputStateLayer.History` gets a dmon-side recall UI in Phase 5 or is dropped.
