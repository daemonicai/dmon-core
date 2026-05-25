## Context

`Dmon.Tui` replaced `Dmon.Console` to solve a streaming-display bug: Spectre.Console's `AnsiConsole.Prompt` blocked stdin, preventing `messageDelta` events from rendering during a turn. The chosen fix (Terminal.Gui v2) introduced new problems — raw-mode terminal takeover, broken copy/paste, intercepted Ctrl+C, and unreliable modal dialogs. The root issue was never Spectre.Console's output capabilities; it was the blocking synchronous prompt call. This change keeps Spectre.Console for rendering and replaces the blocking prompt with an async `Console.ReadKey` loop.

## Goals / Non-Goals

**Goals:**
- Streaming output renders in real time as `messageDelta` events arrive
- Input field never blocks the event channel
- Ctrl+C exits cleanly; OS copy/paste works
- Visual style matches Pi/Claude Code: scrolling output, `─────` horizontal separators, ` > ` prompt, native terminal background
- All slash commands, provider wizard, tool-confirm, UI-input, and session flows preserved

**Non-Goals:**
- Syntax highlighting within code blocks (V1)
- Mouse support
- Cursor addressing / split-view layout (output area is not a fixed-height scrollback viewport)
- Any changes to `Dmon.Core`, protocol, or session storage

## Decisions

### Decision: Spectre.Console for output, raw `Console.ReadKey` for input

**Chosen:** Spectre.Console writes formatted output to stdout. A dedicated `InputReader` class reads keystrokes with `Console.ReadKey(intercept: true)` on a background task and publishes complete lines to a `Channel<string>`.

**Alternatives considered:**
- *Keep Terminal.Gui, restyle it*: Raw-mode intercept problems remain. Ctrl+C and copy/paste cannot be fixed at the application level.
- *Raw ANSI only, no Spectre.Console*: More work for colour and width handling; Spectre.Console's markup and `Rule`/`Markup` types are well-tested and save substantial code.
- *`Console.ReadLine` on background thread*: Blocks a thread; cannot cancel mid-read on .NET; does not support arrow-key editing.

**Rationale:** Spectre.Console's rendering is purely stdout writes — no terminal mode change, no raw mode. The `Console.ReadKey(intercept: true)` loop is the .NET-idiomatic way to build an interactive prompt without blocking stdin.

---

### Decision: Input loop architecture — dedicated background task + Channel

**Chosen:** `InputReader.RunAsync` spins on `Console.ReadKey(intercept: true)` on a background task. Each complete line (Enter pressed) is written to a `Channel<string>`. The main program loop `await`s lines from the channel and dispatches them to `ConsoleEventHandler.HandleUserInputAsync`.

When a turn is active (streaming in progress), `InputReader` keeps accepting keystrokes but does not echo them and discards them (or buffers — see trade-offs). The input prompt is redrawn after the turn ends.

**Rationale:** The channel decouples keystroke reading from command processing. The event channel (`EventDispatcher`) and the input channel are both drained by the same `await` in the main loop via `Task.WhenAny`, keeping everything on a single logical flow.

---

### Decision: Output rendering — print-and-scroll, no cursor addressing

**Chosen:** Output is printed line-by-line to stdout. There is no fixed-height viewport or cursor repositioning. The terminal scrolls naturally. Streaming tokens are appended to the current line in-place using `\r` to overwrite (for short token bursts) and flushed to a new line when a line break arrives or the turn ends.

The settled render (Markdig) reprints the full turn's text with Spectre markup after `TurnEndEvent`. This is visible as a brief "repaint" of the last block, identical to Pi's behaviour.

**Separator and prompt layout:**
```
(output lines scroll up)
──────────────────────────────────────────────────
 > █
──────────────────────────────────────────────────
```
Status (model name, thinking indicator) is shown inline in the separator:
```
──────────────── gemini-2.5-flash  Thinking… ─────
```

**Alternatives considered:**
- *Cursor-addressed split view (fixed output pane + fixed input pane)*: Requires alternate screen buffer and cursor addressing throughout. Complex, fragile on resize, harder to scroll. Deferred to a future polish pass.
- *Clear-and-redraw on every token*: Excessive flicker; unacceptable on slow terminals.

---

### Decision: Wizard and dialogs — numbered inline prompts

**Chosen:** Each wizard step and tool-confirm prompt renders as a numbered list to stdout, then reads a single keypress (digit) or Enter from `InputReader`. No modal dialog loop, no separate application state.

Example:
```
  Select adapter:
    1  anthropic
    2  openai
    3  gemini
  > 
```

`WizardRunner` and `WizardState` are reused unchanged. Each step delegate is rewritten as a simple `Console.Write` + `ReadKeyAsync` sequence.

**Rationale:** Inline prompts are consistent with the Pi/Claude Code aesthetic, require no framework, and work correctly with any terminal emulator.

---

### Decision: Markdown settled rendering via Spectre markup

**Chosen:** Port `MarkdownRenderer` to emit Spectre.Console `Markup` strings instead of `(string, Attribute)` segments. Fenced code blocks render as `Spectre.Console.Panel` with a monospace style. Bold/italic use Spectre markup tags. Bullet lists use `•` prefix.

**Rationale:** Spectre.Console's `AnsiConsole.Write(new Markup(...))` handles ANSI emission, terminal-width wrapping, and colour reset correctly. The Markdig AST-walking logic is unchanged.

---

### Decision: `ConsoleEventHandler` — no `IApplication`/`Invoke` wrapping

**Chosen:** `ConsoleEventHandler` runs on the event-loop background task (same as `TuiEventHandler`). All output is via `TerminalRenderer` which writes directly to `AnsiConsole`. No marshalling to a UI thread is needed because Spectre.Console's `AnsiConsole` is thread-safe for writes (it uses an internal lock).

**Rationale:** Eliminates the entire `IApplication.Invoke` / `BridgeAsync` / `TaskCompletionSource` scaffolding that caused wizard dialog issues in `Dmon.Tui`.

## Risks / Trade-offs

**Risk: Input buffering during streaming** → While a turn is active, keystrokes are discarded. A user who types during streaming will have to retype. Acceptable for V1 — this matches Pi's behaviour. A future improvement could buffer keystrokes and replay them after the turn ends.

**Risk: Settled-render repaint is visible** → When `TurnEndEvent` fires, the last turn's raw text is overwritten with the Markdig-rendered version. Plain prose is identical; fenced code blocks and lists change appearance. Low-impact; same trade-off as the original `tui-migration` design.

**Risk: Terminal width changes mid-session** → Spectre.Console reads `Console.WindowWidth` on each write. Output already rendered is not reflowed. Acceptable for V1.

**Risk: Windows console compatibility** → `Console.ReadKey(intercept: true)` and ANSI escape codes work on Windows 10+ (with VT processing enabled) and all modern terminal emulators. Legacy `cmd.exe` is not supported; not a target platform.

## Migration Plan

1. Add `Dmon.Terminal` project to solution; remove `Dmon.Tui`
2. Copy non-UI files from `Dmon.Tui` unchanged
3. Implement `TerminalRenderer`, `InputReader`, `ConsoleEventHandler`, inline prompt helpers
4. Port `MarkdownRenderer` to Spectre markup
5. Wire `Program.cs` entry point
6. `dotnet build` — zero warnings
7. Manual smoke test: startup → wizard → submit message → stream → settle → tool confirm → `/exit`

No rollback strategy required — host-only change.

## Open Questions

- Should input history (↑/↓) persist across sessions to a file, or be in-memory only? (Assume in-memory for V1.)
- Should the status line show token count if the protocol adds cost events? (Deferred — no cost data today.)
