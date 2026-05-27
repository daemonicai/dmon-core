# terminal-ui

Visual behaviour of the `Dmon.Terminal` host.

## Input block

After `AgentReadyEvent` fires (and after each completed turn), the terminal renders an input block with all chrome **above** the cursor:

```
                              ← blank line
──────────────────────────    ← horizontal rule (full width, no label)
<model name>                  ← status line — present only when a model is active
──────────────────────────    ← horizontal rule (full width, no label)
❯ [cursor]                    ← prompt line — cursor at natural bottom, no repositioning
```

When no model is active the block is 3 lines:

```
                              ← blank line
──────────────────────────    ← horizontal rule
──────────────────────────    ← horizontal rule
❯ [cursor]
```

The cursor rests at the natural end of the printed content. No ANSI cursor-repositioning sequences are emitted.

`_promptBlockLines` tracks the number of lines above the cursor that must be erased during an interrupt. With a model active (status line present) the value is 4 (blank + top rule + status + bottom rule). Without a model active the value is 3 (blank + top rule + bottom rule).

## Interrupt behaviour

If a system message arrives while the input block is active (e.g. an error event, retry notice, or extension event), the renderer:

1. Erases the `❯` line (`\r\x1b[2K`).
2. Erases each chrome line above the cursor (`\x1b[1A\x1b[2K` × `_promptBlockLines`).
3. Prints the system message.
4. Re-renders the input block.
5. Re-echoes the partial input buffer at the cursor position.

The user's in-progress input is preserved and reappears after the system message.

## Submitted input (history)

User input echoed into the session transcript uses the `❯` glyph:

```
❯ what is the capital of france?
```

## Separators

The `──── dmon ────` banner at startup and `──── goodbye ────` at shutdown use labelled rules and are unchanged.

The `──── Thinking… ────` separator on `TurnStartEvent` is unchanged.

No separate separator is rendered between the settled response and the input block; the blank line at the top of the input block provides the visual break.
