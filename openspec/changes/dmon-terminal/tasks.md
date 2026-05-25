## 1. Project Setup

- [x] 1.1 Create `src/Dmon.Terminal/` with `dotnet new console`, set `AssemblyName` to `dmon`, `TreatWarningsAsErrors` to `true`, `Nullable` to `enable`, `ImplicitUsings` to `enable`
- [x] 1.2 Add `Spectre.Console` (latest stable) and `Markdig` (`1.*`) NuGet references
- [x] 1.3 Add project references to `Dmon.Protocol` and `Dmon.Abstractions`
- [x] 1.4 Remove `Dmon.Tui` from `Daemon.slnx`; add `Dmon.Terminal` to `Daemon.slnx`
- [x] 1.5 Copy unchanged from `Dmon.Tui`: `CoreProcessManager.cs`, `EventDispatcher.cs`, `SlashCommandParser.cs`, `AddProviderCommand.cs`, `ToolPermission.cs`, `WizardState.cs`, `WizardRunner.cs` — update namespaces to `Dmon.Terminal`
- [x] 1.6 Verify `dotnet build` passes with zero warnings

## 2. TerminalRenderer

- [x] 2.1 Implement `TerminalRenderer` class: wraps `AnsiConsole`; exposes `AppendToken(string)`, `SettleTurn(string rawText)`, `AddUserLine(string)`, `AddSystemLine(string)`, `PrintSeparator(string? label = null)`, `PrintPrompt()`
- [x] 2.2 `AppendToken`: write token directly to `Console.Out` without newline; track current column position; flush after each token
- [x] 2.3 `SettleTurn`: erase the streamed line(s) using ANSI cursor-up + erase sequences, then write the settled Spectre markup (from `MarkdownRenderer`)
- [x] 2.4 `PrintSeparator`: write a full-width `─` rule; if `label` provided, embed it in the rule using `AnsiConsole.Write(new Rule(label))`
- [x] 2.5 `PrintPrompt`: write `[grey] >[/] ` using Spectre markup; do not write a newline (cursor stays on prompt line)
- [x] 2.6 `AddUserLine`: print `[bold] > {text}[/]` on its own line
- [x] 2.7 `AddSystemLine`: print `[grey]{text}[/]` on its own line
- [x] 2.8 `SetStatus(string modelName, bool thinking)`: stores state used by `PrintSeparator` to build the label

## 3. MarkdownRenderer (Spectre port)

- [x] 3.1 Copy `MarkdownRenderer.cs` from `Dmon.Tui`; change return type from `List<(string Text, Attribute Style)>` to `string` (Spectre markup string)
- [x] 3.2 `ParagraphBlock`: emit plain text with word-wrap (no special markup)
- [x] 3.3 `FencedCodeBlock` / `IndentedCodeBlock`: emit as `[[[italic dim]{language}[/]]]\n{code}` wrapped in a `Panel`-equivalent markup block (use Spectre `[[ ]]` panel syntax or manual border characters)
- [x] 3.4 `ListBlock` / `ListItemBlock`: prefix each item with `  • `
- [x] 3.5 `EmphasisInline` bold: `[bold]{text}[/]`; italic: `[italic]{text}[/]`
- [x] 3.6 `CodeInline`: `[bold yellow on grey]{text}[/]`
- [x] 3.7 `HeadingBlock`: `[bold underline]{text}[/]`

## 4. InputReader

- [ ] 4.1 Implement `InputReader` class: holds a `Channel<string>` and exposes `IAsyncEnumerable<string> ReadLinesAsync(CancellationToken)`
- [ ] 4.2 `RunAsync(CancellationToken)`: loop on `Console.ReadKey(intercept: true)`; build a `StringBuilder` for the current line
- [ ] 4.3 Handle `Enter`: write the buffered line to the channel, clear buffer, write `\n` to move past the prompt
- [ ] 4.4 Handle `Backspace`: if buffer non-empty, remove last char, write `\b \b` to erase the character on screen
- [ ] 4.5 Handle printable characters: append to buffer, echo to `Console.Out`
- [ ] 4.6 Handle `UpArrow` / `DownArrow`: cycle through in-memory history list; rewrite current input line
- [ ] 4.7 Handle `Escape`: clear the current buffer and rewrite (blank) the prompt line
- [ ] 4.8 Expose `bool IsLocked { get; set; }`: when `true`, accept keystrokes but do not echo or enqueue them

## 5. Inline Prompts (Wizard + Tool Confirm)

- [ ] 5.1 Implement `InlinePrompt` static helper: `Task<int?> ChooseAsync(string title, IReadOnlyList<string> options, CancellationToken)` — prints numbered list, reads a single digit keypress (1–9), returns 0-based index or null for cancel (Ctrl+C / `0` / `q`)
- [ ] 5.2 Implement `InlinePrompt.ReadLineAsync(string prompt, bool secret, CancellationToken)` — prints prompt label, reads a line (using `InputReader`-style loop); masks input with `*` when `secret`
- [ ] 5.3 Rewrite `AdapterSelectionStep`: use `InlinePrompt.ChooseAsync` with adapter names; no Terminal.Gui; `b`/`0` returns `WizardState.Back`
- [ ] 5.4 Rewrite `ModelSelectionStep`: use `InlinePrompt.ChooseAsync` with model names for selected adapter
- [ ] 5.5 Rewrite `AuthConfigStep`: use `InlinePrompt.ReadLineAsync` for env-var name; use `InlinePrompt.ChooseAsync` for scope (local/global)
- [ ] 5.6 Implement `ToolConfirmPrompt.ShowAsync(string name, string args, string risk, CancellationToken)`: print tool name/args, print `[red]⚠ HIGH RISK[/]` when risk is `high`, use `InlinePrompt.ChooseAsync` with four options; return `ToolPermission?`

## 6. ConsoleEventHandler

- [ ] 6.1 Implement `ConsoleEventHandler`: constructor takes `TerminalRenderer renderer`, `InputReader input`, `Func<Command, CancellationToken, Task> sendCommand`
- [ ] 6.2 `HandleAsync(Event, CancellationToken)`: switch on event type; all output via `renderer`; no `IApplication`/`Invoke` wrapping
- [ ] 6.3 `TurnStartEvent`: lock input (`input.IsLocked = true`), print separator with `Thinking…` label
- [ ] 6.4 `MessageDeltaEvent`: call `renderer.AppendToken(text)`
- [ ] 6.5 `TurnEndEvent`: call `renderer.SettleTurn(rawText)`, unlock input (`input.IsLocked = false`), reprint separator + prompt
- [ ] 6.6 `ErrorEvent`: `renderer.AddSystemLine("[Error] …")`; if `!Recoverable`, cancel the `CancellationTokenSource`
- [ ] 6.7 `AgentReadyEvent`, `BootstrapNoticeEvent`, `ProviderSwitchedEvent`, `RetryAttemptEvent`, `ExtensionErrorEvent`, `SystemNoticeEvent`, `SessionUpdatedEvent`: emit appropriate system line (port from `TuiEventHandler`)
- [ ] 6.8 `ToolConfirmRequestEvent`: call `ToolConfirmPrompt.ShowAsync`; send `ToolConfirmResponseCommand`
- [ ] 6.9 `UiInputRequestEvent`: call `InlinePrompt.ReadLineAsync`; send `UiInputResponseCommand`
- [ ] 6.10 `SetupRequiredEvent`: lock input, print notice, call `HandleAddProviderAsync`
- [ ] 6.11 `HandleAddProviderAsync`: run `WizardRunner` with rewritten steps; send `ProviderConfigureCommand`; unlock input
- [ ] 6.12 `HandleUserInputAsync(string, CancellationToken)`: parse with `SlashCommandParser`; route exit / client commands / core commands / plain messages (same logic as `TuiEventHandler.HandleUserInputAsync`)

## 7. Program.cs and Wiring

- [ ] 7.1 Implement `Program.cs`: parse `--core-path`; create `CoreProcessManager`, `EventDispatcher`, `TerminalRenderer`, `InputReader`, `ConsoleEventHandler`
- [ ] 7.2 Wire `Console.CancelKeyPress` to `cts.Cancel()` (no `Invoke` needed — Spectre.Console does not intercept signals)
- [ ] 7.3 Start `EventDispatcher.RunAsync(cts.Token)` as background task
- [ ] 7.4 Start `InputReader.RunAsync(cts.Token)` as background task
- [ ] 7.5 Main loop: `await foreach (string line in inputReader.ReadLinesAsync(cts.Token))` → `await handler.HandleUserInputAsync(line, cts.Token)`; also drain the event channel via `Task.WhenAny` so events process concurrently with input
- [ ] 7.6 After main loop exits: `await coreProcess.StopAsync()`; print final separator

## 8. Verification

- [ ] 8.1 `dotnet build` — zero warnings, zero errors (`TreatWarningsAsErrors` enabled)
- [ ] 8.2 Smoke test: startup on clean `~/.dmon` → wizard appears inline → select adapter + model + env var → submit message → response streams token by token → turn settles with markdown → `/exit` closes cleanly
- [ ] 8.3 Smoke test: Ctrl+C exits cleanly at idle and during streaming
- [ ] 8.4 Smoke test: copy/paste works in the terminal (OS-level text selection is not intercepted)
