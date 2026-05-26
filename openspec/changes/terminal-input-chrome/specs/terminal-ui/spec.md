# terminal-ui

Visual behaviour of the `Dmon.Terminal` host.

## Input block

After each turn (and at startup after the `dmon` banner), the terminal renders an input block:

```
                              ← blank line
──────────────────────────    ← horizontal rule (full width, no label)
❯ [cursor]                    ← prompt line
──────────────────────────    ← horizontal rule (full width, no label)
<model name>                  ← status line — omitted when no model active
                              ← blank line (omitted when no model active)
```

The cursor is positioned immediately after `❯ ` (column 2) on the prompt line.

When no model is active the block is 4 lines:

```
                              ← blank line
──────────────────────────    ← horizontal rule
❯ [cursor]
──────────────────────────    ← horizontal rule
```

## Submitted input (history)

User input echoed into the session transcript uses the `❯` glyph:

```
❯ what is the capital of france?
```

## Separators

The `──── dmon ────` banner at startup uses a labelled rule and is unchanged.

Turn-end separators (previously a labelled rule with the model name) are replaced by the input block's leading blank line and top rule. No separate separator is rendered between the settled response and the input block.
