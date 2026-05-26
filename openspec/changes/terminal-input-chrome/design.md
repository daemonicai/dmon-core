## Context

`Dmon.Terminal` uses `TerminalRenderer` to manage all console output and `InputReader` for raw keystroke handling. `PrintPrompt()` currently emits a grey ` > ` prefix and leaves the cursor immediately after it; `InputReader` then echoes characters at that position. The two classes are decoupled — `TerminalRenderer` controls what appears before and around the cursor; `InputReader` handles what the user types.

## Goals / Non-Goals

**Goals:**
- Render a structured input block with horizontal rules above and below the `❯` input line.
- Show the active model name on a status line below the lower rule.
- Omit the status line when no model is active.
- Use `❯` as the prompt glyph consistently (prompt and submitted-input history).

**Non-Goals:**
- Dynamic redraw of the status line mid-input (e.g., model switching while typing).
- Any changes to `Dmon.Tui` or the agent core.
- Cursor movement for multi-line input or terminal-resize handling.

## Decisions

### D1 — Full block rendered upfront, cursor repositioned

`PrintPrompt()` prints the complete block (all lines, top to bottom) then emits ANSI escape sequences to move the cursor back to the `❯` line:

```
\n                           line 1: blank
──────────────────────\n     line 2: top rule
❯ \n                         line 3: input line  ← cursor target
──────────────────────\n     line 4: bottom rule
claude-sonnet-4-6\n          line 5: status (omitted when empty)
\n                           line 6: blank
```

With status present, cursor is at line 7 after printing. Escape sequence: `\x1b[4A` (up 4) + `\x1b[2C` (right 2, past `❯ `).

Without status (model empty), the block is 4 lines (1: blank, 2: top rule, 3: input, 4: bottom rule). Cursor is at line 5. Escape sequence: `\x1b[2A` + `\x1b[2C`.

**Rationale:** `InputReader` uses simple `Console.Write` character echo and backspace-based line replacement. It has no knowledge of the surrounding block. Repositioning the cursor after printing keeps `InputReader` unchanged — it always writes at whatever position the cursor is on.

**Alternative considered:** Render only the top rule + `❯` prefix, leave status below as a post-input render step. Rejected — the status line would appear only after the first keypress, causing a jump.

### D2 — `❯` glyph in `AddUserLine`

Submitted input echoed in the transcript changes from `bold > text` to `bold ❯ text`. This makes the history visually consistent with the live prompt.

### D3 — Remove `PrintSeparator()` from `TurnEndEvent`

Currently `TurnEndEvent` calls `PrintSeparator()` (a labelled horizontal rule with the model name) followed by `PrintPrompt()`. In the new design the model name moves to the status line inside `PrintPrompt()`, and the blank line at the top of the input block provides the visual break. The intermediate `PrintSeparator()` call is removed.

The labelled `PrintSeparator("dmon")` call at startup in `Program.cs` is unaffected.

## Risks / Trade-offs

- **[Long input wrapping]** If the user types a line longer than the terminal width, the cursor wraps onto the bottom-rule line. The input block's visual integrity breaks. Accepted for V1 — the same limitation exists today, and full reflow would require a TUI framework.
- **[ANSI escape support]** Cursor repositioning requires a terminal that honours `\x1b[nA` and `\x1b[nC`. All mainstream terminal emulators on macOS, Linux, and Windows Terminal support these. Accepted.
- **[`❯` glyph rendering]** `❯` (U+276F) is a standard-width Unicode character supported in all modern terminal fonts. Fallback to `>` is not provided — if a font lacks the glyph, the terminal will substitute or render a replacement character. Accepted.
