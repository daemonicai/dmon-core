## 1. TerminalRenderer — input block

- [x] 1.1 Rewrite `PrintPrompt()` to render the 6-line input block (blank, top rule, `❯ `, bottom rule, status, blank) and reposition cursor with ANSI escapes; omit status line and trailing blank when `_modelName` is empty
- [x] 1.2 Update `AddUserLine()` to use `❯` glyph instead of ` > `
- [x] 1.3 Update `_currentLineLength` in `PrintPrompt()` to reflect `❯ ` width (2)

## 2. ConsoleEventHandler — remove redundant separator

- [x] 2.1 Remove `_renderer.PrintSeparator()` call from the `TurnEndEvent` handler

## 3. Verify

- [x] 3.1 Build `Dmon.Terminal` without warnings
- [ ] 3.2 Smoke-test: startup renders input block with no status line; after provider switch the model name appears; submitted input shows `❯` glyph in history
