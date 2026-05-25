## 1. Project Setup

- [x] 1.1 Create `src/Dmon.Tui/` project with `dotnet new classlib`, update to `Exe` output type
- [x] 1.2 Add `Terminal.Gui` (v2.x) and `Markdig` NuGet references to `Dmon.Tui.csproj`
- [x] 1.3 Add project references to `Dmon.Protocol`, `Dmon.Abstractions`
- [x] 1.4 Remove `Dmon.Console` from `Daemon.slnx`; add `Dmon.Tui` to `Daemon.slnx`
- [x] 1.5 Copy `CoreProcessManager.cs`, `EventDispatcher.cs`, `SlashCommandParser.cs` from `Dmon.Console` to `Dmon.Tui` (unchanged — no Spectre dependency)
- [x] 1.6 Ensure that `Dmon.Tui` builds with `dotnet build`
- [x] 1.7 Delete `Dmon.Console` project directory

## 2. TurnBlock Model and ChatOutputView

- [x] 2.1 Define `TurnBlock` class (`ChatRole Role`, `string RawText`, `bool Rendered`) in `Dmon.Tui`
- [x] 2.2 Implement `ChatOutputView : View` holding `List<TurnBlock>`, overriding `OnDrawContent` to render the block list
- [x] 2.3 Implement streaming append: `AppendToken(string token)` adds to the last assistant block, triggers redraw, scrolls to bottom
- [x] 2.4 Implement inline code span detection: scan the tail of `RawText` on each append; apply monospace `Attribute` to completed backtick spans

## 3. Markdig Settled Renderer

- [x] 3.1 Implement `MarkdownRenderer` class: accepts a Markdig `MarkdownDocument`, walks the AST, produces `List<(string Text, Attribute Style)>` segments
- [x] 3.2 Handle `ParagraphBlock`: emit plain text with word wrap
- [x] 3.3 Handle `FencedCodeBlock` / `IndentedCodeBlock`: emit a bordered box with monospace colour attribute
- [x] 3.4 Handle `ListBlock` / `ListItemBlock`: emit `  • ` indent + item text
- [x] 3.5 Handle `EmphasisInline` (bold/italic): emit text with bold or italic colour attribute
- [x] 3.6 Handle `CodeInline`: emit text with monospace colour attribute
- [x] 3.7 Implement `SettleTurn()` on `ChatOutputView`: parse `RawText` with Markdig, replace block display with rendered segments, set `Rendered = true`

## 4. DmonWindow and Status Bar

- [x] 4.1 Implement `DmonWindow : Window` with three zones: `ChatOutputView` (fills upper area), `StatusBar` (one line), `TextField` input (one line at bottom)
- [x] 4.2 Implement status bar: shows active model name + "Thinking" (with indicator) or "Idle"
- [x] 4.3 Wire input `TextField`: on Enter, read text, clear field, delegate to `TuiEventHandler`
- [x] 4.4 Implement input locking: `TextField.Enabled = false` on `TurnStartEvent`, `= true` on `TurnEndEvent`, focus returns to input on re-enable

## 5. TuiEventHandler

- [x] 5.1 Implement `TuiEventHandler` — replaces `ConsoleHost.ProcessEventAsync`; all UI mutations wrapped in `Application.MainLoop.Invoke`
- [x] 5.2 Handle `TurnStartEvent`: append new assistant `TurnBlock`, lock input, set status to Thinking
- [x] 5.3 Handle `MessageDeltaEvent`: call `ChatOutputView.AppendToken`
- [x] 5.4 Handle `TurnEndEvent`: call `ChatOutputView.SettleTurn`, unlock input, set status to Idle, return focus to input field
- [x] 5.5 Handle `ErrorEvent`: display error in output view; cancel application if `!Recoverable`
- [x] 5.6 Handle `ResponseEvent`: display failure message in output view if `!Success`
- [x] 5.7 Handle `AgentReadyEvent`, `BootstrapNoticeEvent`, and remaining event types (port from `EventRenderer`)
- [x] 5.8 Start background `Task` in `DmonWindow` that drains `ChannelReader<Event>` and routes each event through `TuiEventHandler` via `Application.MainLoop.Invoke`

## 6. Modal Dialogs — Tool Confirm and UI Input

- [x] 6.1 Implement `ToolConfirmDialog`: Terminal.Gui `Dialog` with tool name, args, risk level, and four buttons (Allow once / Allow for project / Allow globally / Deny)
- [x] 6.2 Implement high-risk visual distinction in `ToolConfirmDialog` (e.g. red border or warning label when `risk: high`)
- [x] 6.3 Implement `UiInputDialog`: Terminal.Gui `Dialog` with prompt label and `TextField` (or masked `TextField` for `kind: secret`)
- [x] 6.4 Handle `ToolConfirmRequestEvent` in `TuiEventHandler`: show `ToolConfirmDialog`, send `ToolConfirmResponseCommand` with result
- [x] 6.5 Handle `UiInputRequestEvent` in `TuiEventHandler`: show `UiInputDialog`, send `UiInputResponseCommand` with result

## 7. Setup Wizard — Step-Dialog Sequence

- [x] 7.1 Define `WizardState` record (`string? Adapter`, `string? ModelId`, `string? EnvVar`, `string? Scope`)
- [x] 7.2 Implement wizard step runner: iterates `List<Func<WizardState, Task<WizardState?>>>`, maintains prior-state stack for Back navigation
- [x] 7.3 Implement `AdapterSelectionStep`: `Dialog` with `ListView` of adapter names
- [x] 7.4 Implement `ModelSelectionStep`: `Dialog` with `ListView` of models for the selected adapter
- [x] 7.5 Implement `AuthConfigStep`: `Dialog` with env-var-name `TextField` or direct-entry option
- [x] 7.6 Wire wizard into `DmonWindow` startup flow (replaces `SetupWizard.Show`) and `/add-provider` slash command handler

## 8. Wiring and Verification

- [x] 8.1 Implement `Program.cs` entry point: create `IApplication`, init, run `DmonWindow`, dispose
- [x] 8.2 Wire `CancellationToken` from Ctrl+C to `Application.RequestStop`
- [x] 8.3 Port all slash command handling from `ConsoleHost.ProcessUserInputAsync` to `TuiEventHandler` (session, model, thinking, fork, exit)
- [x] 8.4 Port `HandleAddProviderAsync` restart-and-wait logic to `TuiEventHandler`
- [x] 8.5 Verify `dotnet build` passes with zero warnings (`TreatWarningsAsErrors` enabled)
- [ ] 8.6 Smoke test: startup → submit message → response streams and settles → tool confirm dialog → `/exit`
