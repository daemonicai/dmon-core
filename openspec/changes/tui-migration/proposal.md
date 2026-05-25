## Why

The `Dmon.Console` host is broken for its primary use case: agent responses are never displayed. `MainLoopAsync` uses `TryRead` (non-blocking) so it falls through to `AnsiConsole.Prompt` before the core has responded — `Prompt` then owns stdin, blocking the channel reader from draining, so events accumulate unseen until the user types something else. This is a fundamental limitation of Spectre.Console's synchronous input model and cannot be fixed within it.

## What Changes

- **BREAKING**: `Dmon.Console` project renamed to `Dmon.Tui`; `Spectre.Console` dependency removed
- Terminal.Gui v2 replaces Spectre.Console as the UI toolkit
- `DmonWindow` replaces `ConsoleHost` as the top-level UI component
- `ChatOutputView` introduced: a list-of-turn-blocks model with streaming and settled rendering
- Input `TextField` is disabled while a turn is active and re-enabled on `TurnEndEvent`
- Markdown rendered on turn completion via Markdig (stream raw, settle on `TurnEndEvent`)
- Tool confirm prompts become Terminal.Gui modal dialogs
- Setup wizard redesigned as a sequence of step dialogs (Back/Next) replacing the monolithic Spectre prompt
- Status bar shows active model and agent state (Thinking / Idle)
- `EventDispatcher`, `CoreProcessManager`, and the Channel plumbing are unchanged

## Capabilities

### New Capabilities

- `tui-rendering`: The `ChatOutputView` rendering pipeline — `TurnBlock` model, streaming raw append with inline code colouring, Markdig-based settled rendering on `TurnEndEvent`

### Modified Capabilities

- `console-host`: Implementation requirement changes from Spectre.Console to Terminal.Gui v2; adds input-locking during turns; wizard redesigned as step-dialog sequence; tool confirms become modal dialogs

## Impact

- `src/Dmon.Console/` → `src/Dmon.Tui/` (project rename, all files replaced)
- New NuGet dependencies: `Terminal.Gui`, `Markdig`
- Removed NuGet dependency: `Spectre.Console`
- `Daemon.slnx` updated to reference renamed project
- All existing console host behaviour (slash commands, tool confirm, UI input, session management, provider switching) is preserved; only the rendering and input mechanism changes
