## ADDED Requirements

### Requirement: Terminal substrate is `dcli`

The terminal host SHALL use the `dcli` library's `ITerminal` façade (and `Dcli.Testing.HeadlessTerminal` for tests) as the substrate for all rendering, input handling, dialog presentation, and status display. The terminal host SHALL NOT depend on `Spectre.Console` and SHALL NOT call `System.Console.ReadKey` / `Console.Write` / `Console.SetCursorPosition` directly in any code that renders or accepts user input.

The mapping of terminal-host concerns to `dcli` surfaces SHALL be:

- Streaming token output → `ITerminal.Scrollback.BeginLive` + `AppendText` + `Commit`.
- Settled markdown render → `ITerminal.Scrollback.Append(Line)` with lines produced by the markdown renderer.
- Horizontal separator lines → `ITerminal.Scrollback.Append(Line)` with the rule glyph composed inline (until a future `Scrollback.AppendRule` surfaces in `dcli`).
- Status indicator (model name, mode, working/idle state) → `ITerminal.Status.SetRows`.
- Wizard step prompts (`SelectAdapter`, `SelectModel`, free-text auth) → `await ITerminal.SelectAsync` / `await ITerminal.InputAsync`.
- Tool confirmation prompt → `await ITerminal.ChoiceAsync`.
- User input → subscribe to `ITerminal.Events` for `InputSubmitted` and `InputChanged` events.
- Locked-input semantics (while a turn is in flight) SHALL be implemented in dmon as a state layer above `dcli.Input` — the terminal host SHALL drop or flash a notice for `InputSubmitted` events received while its `IsLocked` flag is `true`. Once `dcli.Input.ReadOnly` ships, the terminal host SHOULD migrate to that primitive.

The terminal host SHALL be unit-testable by substituting `ITerminal` with either a hand-rolled tier-A fake or `Dcli.Testing.HeadlessTerminal`.

#### Scenario: No Spectre.Console dependency

- **WHEN** the `Dmon.Terminal` project is built
- **THEN** its package graph (including transitive dependencies pulled by `Dmon.Terminal` itself) SHALL NOT include `Spectre.Console`

#### Scenario: Tier-A fake drives the renderer

- **WHEN** a unit test substitutes a hand-rolled `ITerminal` fake for the live terminal
- **THEN** every command the renderer issues (scrollback append, status set, dialog open) is recorded on the fake and assertable, without spinning a real terminal or a real render loop

#### Scenario: Headless harness drives integration tests

- **WHEN** an integration test uses `Dcli.Testing.HeadlessTerminal` to host the renderer
- **THEN** the real `dcli` render loop, layout, and overlay routing run against the test's scripted input and produce assertable frame snapshots

#### Scenario: Locked input dropped during a turn

- **WHEN** the terminal host's `IsLocked` flag is `true` and `ITerminal.Events` emits an `InputSubmitted` event
- **THEN** the host drops the submission, does not forward it to the core, and optionally surfaces a "still working" indicator via `Status.SetRows`

## MODIFIED Requirements

### Requirement: Streaming output renders in real time

The terminal host SHALL display `messageDelta` tokens to the user as they arrive without buffering, by streaming them into a `dcli` live scrollback block (`Scrollback.BeginLive` → `AppendText` per token). The input prompt SHALL remain responsive (accepting keystrokes for display, with `InputSubmitted` events dropped while `IsLocked=true`) while a turn is active.

#### Scenario: Tokens appear as they stream

- **WHEN** the core emits a sequence of `messageDelta` events during a turn
- **THEN** each token is appended to the active live block immediately and is visible to the user without waiting for the turn to end

#### Scenario: Input discarded during streaming

- **WHEN** the user types while a turn is active and `IsLocked=true`
- **THEN** keystrokes still echo into `dcli`'s input editor (so the user sees what they are typing) but submissions on Enter are dropped by the host and no partial input is forwarded to the core

#### Scenario: Live block commits on turn end

- **WHEN** `TurnEndEvent` is received
- **THEN** the host commits the live scrollback block, which freezes the streamed content into native scrollback and releases the live-window slot for the next turn

### Requirement: Settled markdown render on turn end

The terminal host SHALL re-render the completed turn with full Markdig markdown styling when `TurnEndEvent` is received, by producing a settled `IReadOnlyList<Line>` from the markdown source via a pure markdown-to-`Line[]` transform and appending those lines via `ITerminal.Scrollback.Append`.

#### Scenario: Fenced code block rendered with border

- **WHEN** a completed turn contains a fenced code block
- **THEN** the settled render presents the code block with the conventional styled border (background tint or border characters), produced as a sequence of `Line`s from the markdown transform

#### Scenario: Settled lines replace the live block

- **WHEN** the live streamed block has been committed and the settled markdown render begins
- **THEN** the settled lines are appended to the scrollback in order, succeeding the streamed (now-committed) tokens

#### Scenario: Markdown transform is a pure function

- **WHEN** a unit test invokes the markdown renderer with a markdown source string
- **THEN** the renderer returns an `IReadOnlyList<Line>` deterministically, with no I/O, no `ITerminal` reference, and no global console state

### Requirement: Input prompt with horizontal separators

The terminal host SHALL present the input zone with the input editor between two horizontal separator rows, the top separator showing model + mode + state indicators (`Thinking…` or `Idle`). The separators and status SHALL be rendered as `dcli` fixed-region rows — the input via `ITerminal.Input` (cursor and editing owned by `dcli`), the separators and status via `ITerminal.Status.SetRows`.

#### Scenario: Prompt displayed when idle

- **WHEN** no turn is active
- **THEN** the fixed region shows a top status row containing the separator + status text, the `dcli` input editor with cursor, and a bottom status row containing a separator

#### Scenario: Status shown in top separator

- **WHEN** the active model name is known
- **THEN** the top status row includes the model name and `Thinking…` or `Idle` indicator, set via `Status.SetRows`

### Requirement: Ctrl+C exits cleanly

The terminal host SHALL handle Ctrl+C via `dcli`'s `Events.KeyPressed` event (which surfaces `KeyEvent(Char('c'), Modifiers.Ctrl)` per `dcli`'s key-encoding contract — `dcli` does not terminate the process for the consumer) and SHALL perform a graceful shutdown: stop the core process, cancel background tasks, exit with code 0.

#### Scenario: Ctrl+C during idle

- **WHEN** the user presses Ctrl+C while no turn is active and `dcli` emits `KeyPressed(Char('c'), Ctrl)`
- **THEN** the host stops the core process and exits

#### Scenario: Ctrl+C during streaming

- **WHEN** the user presses Ctrl+C while a turn is streaming and `dcli` emits `KeyPressed(Char('c'), Ctrl)`
- **THEN** the host cancels the active turn's `CancellationToken`, stops the core process, and exits

### Requirement: OS copy/paste works normally

The terminal host SHALL rely on `dcli`'s inline rendering (which preserves the terminal's native scrollback per `dcli`'s Decision 1) so the operating system's native text-selection and copy/paste functionality remains available. The host SHALL NOT enable an alternate-screen buffer.

#### Scenario: Text selection in terminal

- **WHEN** the user drags to select output text in their terminal emulator
- **THEN** the selection is handled by the terminal emulator; `dcli` does not own the alternate screen, so committed scrollback is fully selectable and copyable

### Requirement: Inline wizard prompts

The terminal host SHALL present provider setup and `/add-provider` wizard steps via `dcli`'s awaitable dialog methods (`await ITerminal.SelectAsync` for list pickers, `await ITerminal.InputAsync` for free-text and secret inputs). The wizard step order SHALL be: (1) Select Adapter, (2) Auth Configuration, (3) Select Model. The model selection step SHALL call `IProviderFactory.GetAvailableModelsAsync` with the API key resolved from the env var entered in step 2. If the live fetch fails or returns an empty list, the step SHALL fall back to the factory's static model list. A brief `Fetching models…` status SHALL be shown via `Status.SetRows` while the fetch is in progress. Multi-step pickers (adapter, model) SHALL be opened with `SelectRequest { AllowBack = true }` so the user can navigate back via Backspace.

#### Scenario: Adapter selection prompt

- **WHEN** the wizard is at the adapter-selection step (step 1)
- **THEN** the host shows a `dcli` `SelectAsync` overlay listing available adapters with arrow-key navigation and Enter to select

#### Scenario: Auth config precedes model selection

- **WHEN** the user completes step 1 (adapter)
- **THEN** the wizard shows the auth configuration `InputAsync` prompt (step 2) before the model selection `SelectAsync` prompt (step 3)

#### Scenario: Live model list shown when key resolves

- **WHEN** the user completes step 2 and the env var they entered is set in the environment
- **THEN** step 3 shows the live model list fetched from the provider, with `Fetching models…` displayed via `Status.SetRows` while the call is in flight

#### Scenario: Static fallback shown when fetch fails

- **WHEN** the live model fetch in step 3 fails for any reason
- **THEN** the wizard shows the static fallback model list and continues normally without surfacing an error

#### Scenario: Back navigation via Backspace

- **WHEN** the user presses Backspace at a wizard step opened with `AllowBack = true` and before moving the selection
- **THEN** the dialog returns `DialogOutcome.Back` and the wizard returns to the previous step (using the existing back-stack)

#### Scenario: Wizard cancellation via Escape

- **WHEN** the user presses Escape during a wizard step
- **THEN** the dialog returns `DialogOutcome.Cancelled`, the wizard is cancelled, a notice is shown, and the input prompt is restored

### Requirement: Inline tool confirmation prompt

The terminal host SHALL present `tool.confirmRequest` via `dcli`'s `await ITerminal.ChoiceAsync` with four options: Allow once / Allow for project / Allow globally / Deny. The prompt SHALL show the tool name, arguments, and risk level; high-risk prompts SHALL include a styled `⚠ HIGH RISK` line as part of the request's prompt content.

#### Scenario: Tool confirm prompt displayed

- **WHEN** the core emits `tool.confirmRequest`
- **THEN** the host opens a `ChoiceAsync` overlay displaying the tool name, args, and risk level, with four numbered options: Allow once / Allow for project / Allow globally / Deny

#### Scenario: High-risk prompt visually distinct

- **WHEN** `risk` is `high`
- **THEN** the `ChoiceRequest`'s prompt includes a styled `⚠ HIGH RISK` line (red bold) before the option list

#### Scenario: Response relayed to core

- **WHEN** the user submits an option via the dialog
- **THEN** the dialog's `DialogResult<int>.Value` selects the appropriate `confirmed` flag and `scope`, and the host sends `tool.confirmResponse` to the core
