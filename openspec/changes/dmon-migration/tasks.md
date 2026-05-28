## 1. Wire up `dcli`; port `TerminalRenderer`

- [x] 1.1 Add a `<ProjectReference>` to `../../../dcli/src/Dcli/Dcli.csproj` (and `Dcli.Testing.csproj` for tests) in `src/Dmon.Terminal/Dmon.Terminal.csproj` and the corresponding test csproj. (Will swap to `<PackageReference Version="0.2.0-rc.x" />` before the migration's PR opens, once `api-ergonomics-pass-1` ships.)
- [x] 1.2 Bootstrap a single `ITerminal` instance per process in `Program.cs` via `await Terminal.StartAsync(new TerminalOptions())`; pass it down to renderer + handlers via constructor injection (no statics)
- [x] 1.3 Port `TerminalRenderer.cs`: replace `Console.Write(token)` (`messageDelta`) with `Scrollback.BeginLive().AppendText(token)` per-token, `Commit()` on `TurnEndEvent`; replace `AnsiConsole.Write(Rule)` (separator rule) with `Scrollback.Append(Line)` using composed `─` runs; replace `AnsiConsole.MarkupLine` (status lines) with `Status.SetRows`; remove direct `Console.Write("\r\x1b[2K")` cursor-clear sequences. **Phase 1 deferral:** `SettleTurn` keeps its `string` parameter for call-site compatibility but ignores it — the committed live block (raw streamed tokens) is the final Phase 1 render. Phase 5 restores styled re-render by populating the live block via `SetContent(Line[])` before `Commit`.
- [x] 1.4 Tier-A test `test/Dmon.Terminal.Tests/TerminalRendererTests.cs` (new file): hand-rolled fake `ITerminal` records scrollback / status / dialog calls; assert that a stream of `messageDelta` events produces the expected `AppendText` sequence, `TurnEndEvent` triggers `Commit`, and a model change updates `Status.SetRows`
- [x] 1.5 Tier-B test `test/Dmon.Terminal.Tests/TerminalRendererHarnessTests.cs` (new file): one integration test using `Dcli.Testing.HeadlessTerminal` to assert the rendered `FrameSnapshot` after a representative streaming sequence
- [x] 1.6 Manual smoke (Phase 1 limited scope): `make build && build/dmon` launches without crash, dcli's fixed region renders (status row + input editor visible), Ctrl+C exits cleanly. **End-to-end smoke (input → turn → streaming → settle → status update) is impossible until Phase 4 lands the `Events.InputSubmitted` subscription** — the legacy `InputReader`'s stdin polling is starved by dcli's raw-mode parser, and nothing in Phase 1 reads dcli's input events. Until Phase 4, the renderer port is verified by the tier-A and tier-B tests in §1.4/§1.5; the same end-to-end blocker will apply to §2.8 and §3.6.
- [x] 1.7 Standard gates: `dotnet build`, `dotnet test`, `openspec validate dmon-migration --strict`; reviewer audit; commit

## 2. Port dialog surfaces: `InlinePrompt` / `ToolConfirmPrompt` / `WizardEngine`

- [ ] 2.1 Delete `src/Dmon.Terminal/WizardRenderer.cs` — its responsibilities collapse into the wizard's direct dialog calls
- [ ] 2.2 Delete `src/Dmon.Terminal/InlinePrompt.cs` — its `ChooseAsync` callers move to `await terminal.SelectAsync(new SelectRequest(...))`; its `ReadLineAsync` callers move to `await terminal.InputAsync(new InputRequest(...))`
- [ ] 2.3 Port `WizardEngine.cs`: drop the injected `Func<WizardStep, Outcome>` delegate parameter (the test-quarantine hack — no longer needed since `ITerminal` is fakeable); call `terminal.SelectAsync` / `terminal.InputAsync` directly inside the engine; preserve the back-stack list and all step-ordering logic byte-for-byte. Reference implementation: `dcli/samples/Dcli.Demo.DmonWizard/Engine/WizardEngine.cs`
- [ ] 2.4 For multi-step pickers (adapter, model), construct `SelectRequest` with `AllowBack = true` so Backspace navigates back to the previous step (replaces the synthetic "← Back" item at index 0)
- [ ] 2.5 Port `ToolConfirmPrompt.cs`: replace `InlinePrompt.ChooseAsync` with `await terminal.ChoiceAsync(new ChoiceRequest(promptLines, options, allowBack: false))`; compose the high-risk indicator as a `Line` inside the `ChoiceRequest`'s prompt content
- [ ] 2.6 Update `WizardEngine` tests: drop the fake-`renderStep` delegate, use a tier-A `ITerminal` fake that returns scripted `DialogResult`s; assert back-stack behaviour, cancellation, step ordering, and `AllowBack`-driven back navigation
- [ ] 2.7 New tier-A tests for tool-confirm + each input-request flow
- [ ] 2.8 Manual smoke: run the provider-setup wizard end-to-end; trigger a tool confirm; verify visual + interaction parity with the pre-port behaviour
- [ ] 2.9 Gates + reviewer + commit

## 3. Adapter: `ConsoleEventHandler`

- [ ] 3.1 Delete `src/Dmon.Terminal/AddProviderCommand.cs` and `ReloadCommand.cs` (empty marker records, semantics now live in the adapter dispatch)
- [ ] 3.2 Refactor `ConsoleEventHandler.cs` into a thin adapter: incoming RPC events (`ToolConfirmRequest`, `UiInputRequest`, `WizardStepRequest`, `MessageDelta`, `TurnEndEvent`, `BootstrapNotice`, `ToolStarted/Completed`) route to the appropriate `dcli` calls (`terminal.ChoiceAsync`, `terminal.InputAsync`, `WizardEngine.RunAsync`, `terminal.Scrollback.*`, `terminal.Status.SetRows`); no direct console writes
- [ ] 3.3 Wire `dcli`'s `Events.InputSubmitted` into the adapter: parse slash commands locally (`SlashCommandParser`) and forward to the core via the existing RPC channel
- [ ] 3.4 Wire `Events.KeyPressed(KeyEvent(Char('c'), Ctrl))` to the graceful-shutdown path (replaces the `Console.CancelKeyPress` handler — keep that handler as a redundancy net for SIGINT delivered outside dcli's input stream, but it now just delegates to the same shutdown method)
- [ ] 3.5 Tier-A tests for the adapter: hand-rolled `ITerminal` fake; synthesise RPC events; assert the adapter calls the expected `dcli` methods with the expected arguments
- [ ] 3.6 Manual smoke: run the full app, hit every RPC event type, verify correct dispatch
- [ ] 3.7 Gates + reviewer + commit

## 4. State layer: `InputReader`

- [ ] 4.1 Refactor `src/Dmon.Terminal/InputReader.cs` (or rename to something like `InputStateLayer.cs`): remove the dedicated input thread and the `stdin` polling; remove the internal `ChannelReader<string>` (callers subscribe to dcli's `Events` instead, or to a thin dmon-side wrapper)
- [ ] 4.2 Preserve: the `History` deque (capacity-bounded), the `IsLocked` flag, and any `CurrentBuffer` mirror used by other parts of dmon. `History` is updated on `InputSubmitted` (when not locked); `CurrentBuffer` is updated on `InputChanged`
- [ ] 4.3 Implement locked-input enforcement: when `IsLocked && InputSubmitted` arrives, drop the submission (do not forward to the core); optionally call `Status.SetRows` with a "still working" indicator that auto-clears on `TurnEndEvent`
- [ ] 4.4 Tier-A tests: scripted `Events` stream + `IsLocked` transitions; assert history is appended only on accepted submissions, locked submissions are dropped, `CurrentBuffer` mirrors `InputChanged`
- [ ] 4.5 Manual smoke: turn in flight + typing → echo visible, submit on Enter blocked; turn ends → submit re-enabled
- [ ] 4.6 Gates + reviewer + commit

## 5. `MarkdownRenderer` rewrite + drop `Spectre.Console`

- [ ] 5.1 **Prerequisite:** `dcli 0.2.0-rc.x` available (via NuGet feed or local project ref); validate `Line.FromText`, string-accepting `Scrollback.Append`, and the `*Request` string overloads are present
- [ ] 5.2 Rewrite `src/Dmon.Terminal/MarkdownRenderer.cs`: change output type from `string` (Spectre markup) to `IReadOnlyList<Line>`; consume Markdig's AST and emit styled `Segment`s/`Line`s directly (header levels → bold + colour, fenced code → background tint + border characters, links → underline + colour, lists → indent + bullet glyph). No `string`-of-markup intermediate
- [ ] 5.3 Update callers: wherever the old `MarkdownRenderer.Render(md) → string` then `AnsiConsole.MarkupLine(s)` pattern was used, replace with `var lines = MarkdownRenderer.Render(md); foreach (var l in lines) terminal.Scrollback.Append(l);`
- [ ] 5.4 Pure-function tests for `MarkdownRenderer`: markdown fixtures in / `Line[]` out; assert per-line `Segment.Style` against expected styling for headers / code blocks / links / lists
- [ ] 5.5 Update `Dmon.Terminal.csproj`: remove `<PackageReference Include="Spectre.Console" />`; swap any local `<ProjectReference>` to dcli with a `<PackageReference Include="dcli" Version="0.2.0-rc.x" />` and same for `Dcli.Testing` in the test csproj
- [ ] 5.6 Confirm Spectre is not pulled transitively: `dotnet list package --include-transitive | grep -i spectre` returns nothing
- [ ] 5.7 Manual smoke: render a representative assistant turn with rich markdown (headers, code blocks, lists, inline styling); compare visual against pre-migration screenshots; flag any visual regressions for follow-up
- [ ] 5.8 Update README and any CONTRIBUTING / dev-setup docs that referenced Spectre
- [ ] 5.9 Gates + reviewer + commit; archive the change
