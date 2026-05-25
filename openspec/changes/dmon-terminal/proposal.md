## Why

`Dmon.Tui` (Terminal.Gui v2) puts the terminal in raw mode, intercepting Ctrl+C, breaking OS-level copy/paste, and producing a windowed look-and-feel that does not match the minimal aesthetic of tools like Pi and Claude Code. The framework's modal event loop also made the setup wizard unreliable. A direct ANSI/Spectre.Console approach gives us the same streaming capability with none of these problems.

## What Changes

- **NEW** `Dmon.Terminal` project replaces `Dmon.Tui` in the solution
- **REMOVED** Terminal.Gui v2 dependency; Spectre.Console re-added for output rendering only
- **NEW** `TerminalRenderer` — writes coloured, markdown-styled output to stdout using Spectre.Console markup; horizontal `Rule` separators; no raw-mode takeover
- **NEW** `InputReader` — async character-by-character input loop using `Console.ReadKey(intercept: true)`; handles backspace, left/right arrows, history (↑/↓), Ctrl+C; does not block the event channel
- **NEW** `ConsoleEventHandler` — replaces `TuiEventHandler`; no `IApplication` / `Invoke` wrapping; writes directly to `TerminalRenderer`
- **NEW** inline text prompts for wizard steps and tool-confirm dialogs (numbered list, type a number); replaces Terminal.Gui `Dialog` widgets
- **REUSED** `CoreProcessManager`, `EventDispatcher`, `SlashCommandParser`, `WizardState`, `WizardRunner`, `ToolPermission`, `AddProviderCommand` copied unchanged
- `MarkdownRenderer` Markdig AST walker ported to emit Spectre markup strings instead of Terminal.Gui `Attribute` segments

## Capabilities

### New Capabilities

- `terminal-host`: The `Dmon.Terminal` console host — entry point, input loop, output rendering, event routing, Ctrl+C shutdown

### Modified Capabilities

- `console-host`: Replaces the Spectre.Console-blocking host and the Terminal.Gui host with the new ANSI/Spectre rendering host

## Impact

- `Dmon.Tui` removed from `Daemon.slnx`; `Dmon.Terminal` added
- Terminal.Gui NuGet dependency removed; Spectre.Console re-added (`Spectre.Console` latest stable)
- No changes to `Dmon.Core`, `Dmon.Protocol`, `Dmon.Abstractions`, session storage, or the RPC protocol
- All existing slash commands, provider wizard, tool-confirm, and UI-input flows preserved
