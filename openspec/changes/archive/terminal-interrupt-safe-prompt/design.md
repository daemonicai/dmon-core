## Context

`terminal-input-chrome` rendered a 4–6 line input block and used ANSI cursor-up sequences to reposition the cursor mid-block. Any subsequent write corrupts the block because the terminal writes at the current cursor position. The fix is to eliminate the repositioning by placing all chrome above the cursor.

## Goals / Non-Goals

**Goals:**
- All chrome lines are above the cursor; no ANSI repositioning after `PrintPrompt()`.
- Any write method that fires while the prompt is active erases the block and re-renders it after writing.
- Partial input buffer is preserved and re-echoed after an interrupt.

**Non-Goals:**
- Full reflow on terminal resize.
- Handling input longer than one terminal width (wrap edge case accepted).
- Any changes to `Dmon.Tui` or the agent core.

## Decisions

### D1 — Flip the layout: chrome above cursor

```
WITH STATUS (4 lines):          WITHOUT STATUS (3 lines):

(blank)                         (blank)
──────────────────────          ──────────────────────
claude-sonnet-4-6               ──────────────────────
──────────────────────          ❯ [cursor]
❯ [cursor]
```

`PrintPrompt()` prints all lines in order, top to bottom. The cursor ends up immediately after `❯ ` at the natural bottom. No `\x1b[nA]` needed.

`_promptBlockLines` is set to `4` (status present) or `3` (status absent) at `PrintPrompt()` time. The current line (`❯`) is not counted in `_promptBlockLines` — it is cleared separately by `InterruptPrompt()`.

**Rationale:** All lines to erase during an interrupt are *above* the cursor. `\x1b[1A\x1b[2K]` repeated `_promptBlockLines` times clears them without any downward movement. Downward movement risks scrolling the terminal and is avoided entirely.

### D2 — `InterruptPrompt()`: erase upward, reset flag

```csharp
private void InterruptPrompt()
{
    if (!_promptActive) return;
    Console.Write("\r\x1b[2K");                          // clear ❯ line
    for (int i = 0; i < _promptBlockLines; i++)
        Console.Write("\x1b[1A\x1b[2K");                 // up + clear, N times
    _promptActive = false;
}
```

After `InterruptPrompt()` the cursor is at column 0 of the blank line that preceded the block — effectively where the cursor was before `PrintPrompt()` was first called. The next write proceeds naturally.

### D3 — Re-render and re-echo after interrupt

Write methods that can fire mid-input (all of them, conservatively) follow this pattern:

```csharp
InterruptPrompt();
// ... write the content ...
if (shouldRerender)
{
    PrintPrompt();
    string buffer = _getBuffer?.Invoke() ?? string.Empty;
    if (buffer.Length > 0)
        Console.Write(buffer);
}
```

`shouldRerender` is true for all methods except those that themselves call `PrintPrompt()` (e.g. the `TurnEndEvent` path which calls `PrintPrompt()` explicitly).

The buffer re-echo writes the partial input string directly to `Console`. Because `InputReader` is already tracking the buffer internally, no state is lost — only the visual representation is restored.

### D4 — `InputReader.CurrentBuffer`: snapshot property

```csharp
public string CurrentBuffer => _buffer.ToString();
```

`_buffer` is the existing `StringBuilder` in `InputReader`. The property is a snapshot (allocates a string); it is only called at interrupt time, not on every keypress, so allocation cost is negligible.

### D5 — Injection via `Func<string>?`

`TerminalRenderer` accepts an optional `Func<string>? getBuffer` parameter. If null, re-echo is skipped (useful in tests or contexts without an `InputReader`). `Program.cs` passes `() => inputReader.CurrentBuffer`.

`TerminalRenderer` does not take a direct `InputReader` dependency — the delegate keeps the types decoupled.

## Risks / Trade-offs

- **[Interrupt during streaming]** `TurnStartEvent` locks `InputReader` and `AppendToken` fires repeatedly. `_promptActive` is false during streaming (the prompt is not shown during a turn), so `InterruptPrompt()` is a no-op. No risk.
- **[Re-render after every system line]** If multiple system lines fire in rapid succession (e.g. bootstrap + ready), each triggers an interrupt + re-render cycle. Visually this is a flicker but functionally correct. Accepted for V1.
- **[Buffer snapshot cost]** `CurrentBuffer` allocates a string on each interrupt. Interrupts are rare (async events mid-input); cost is negligible.
- **[Line count coupling]** `_promptBlockLines` must stay in sync with the number of lines `PrintPrompt()` actually emits. Both are in the same method — co-location mitigates drift.
