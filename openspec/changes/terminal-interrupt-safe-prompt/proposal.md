## Why

The `terminal-input-chrome` change introduced a structured input block rendered around the `❯` prompt. Because the block is printed top-to-bottom and the cursor is then repositioned mid-block, any system message (`AddSystemLine`, `AddUserLine`, `PrintSeparator`) that fires while the prompt is active writes at the cursor position — corrupting the `❯` line and the pre-printed chrome below it. This happens in practice for errors, retries, and any other async event that arrives between turns.

## What Changes

- Flip the input block layout so all chrome is **above** the cursor: the cursor rests at the natural bottom after printing, with no ANSI repositioning needed.
- `TerminalRenderer` tracks whether the prompt block is active (`_promptActive`) and how many lines it occupies (`_promptBlockLines`).
- All `TerminalRenderer` write methods call `InterruptPrompt()` before writing: if the prompt is active, `InterruptPrompt()` erases the block upward (cursor-up + erase-line, repeated `_promptBlockLines` times), clears `_promptActive`, then the write proceeds normally.
- After writing, methods that can fire mid-input re-render the prompt and re-echo the partial input buffer.
- `InputReader` exposes `string CurrentBuffer { get; }` — a snapshot of the in-progress line.
- `TerminalRenderer` accepts a `Func<string>?` delegate at construction (`getBuffer`); `Program.cs` supplies `() => inputReader.CurrentBuffer`.

## Capabilities

### Modified Capabilities

- `terminal-ui`: Input block layout flipped (chrome above cursor); prompt is interrupt-safe.

## Impact

- **`Dmon.Terminal/TerminalRenderer.cs`**: layout change, `_promptActive` / `_promptBlockLines` tracking, `InterruptPrompt()` method, `Func<string>?` constructor parameter.
- **`Dmon.Terminal/InputReader.cs`**: add `CurrentBuffer` property.
- **`Dmon.Terminal/Program.cs`**: pass `() => inputReader.CurrentBuffer` to `TerminalRenderer`.
- **No impact** on `Dmon.Tui`, the agent core, RPC protocol, or any other component.
