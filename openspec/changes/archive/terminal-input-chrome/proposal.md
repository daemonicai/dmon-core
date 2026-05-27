## Why

The terminal input area currently has no visual chrome: the prompt is a bare grey ` > ` prefix and blends into surrounding output. A structured input block — bounded by horizontal rules, using `❯` as the prompt glyph, with the active model shown below — makes the input area instantly legible and gives the session a clear visual rhythm.

## What Changes

- `TerminalRenderer.PrintPrompt()` renders a 6-line input block (blank → rule → `❯ ` → rule → model status → blank) and repositions the cursor to the `❯` line via ANSI escape sequences.
- `TerminalRenderer.AddUserLine()` changes the submitted-input glyph from ` > ` to `❯` for consistency with the prompt.
- The redundant `PrintSeparator()` call in `ConsoleEventHandler.TurnEndEvent` is removed; the blank line in the new `PrintPrompt()` block provides the visual break.
- When no model is active (`_modelName` is empty), the status line and its trailing blank are omitted, reducing the block to 4 lines (blank → rule → `❯ ` → rule).

## Capabilities

### Modified Capabilities

- `terminal-ui`: Visual appearance of the input area in `Dmon.Terminal`.

## Impact

- **`Dmon.Terminal/TerminalRenderer.cs`**: `PrintPrompt()` and `AddUserLine()` rewritten.
- **`Dmon.Terminal/ConsoleEventHandler.cs`**: Remove `_renderer.PrintSeparator()` call in `TurnEndEvent` handler.
- **No impact** on `Dmon.Tui`, the agent core, the RPC protocol, or any other component.
