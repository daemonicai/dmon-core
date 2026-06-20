## Purpose

Defines the terminal (console) host: how it renders streaming output, accepts user input and slash commands, supervises the `Dmon.Core` process over JSONL/stdio, and applies configuration changes by restarting the core via `/reload`.
## Requirements
### Requirement: Startup welcome banner and MOTD

The terminal host SHALL, on startup, write an ASCII `dmon` banner and a tagline MOTD to scrollback via `ITerminal.Scrollback.Append`, replacing the prior bare `dmon` separator. The banner and tagline SHALL be emitted into scrollback (so they scroll away with history) rather than pinned in the fixed region, and SHALL appear before the first input prompt is presented.

#### Scenario: Banner and tagline shown at startup

- **WHEN** the terminal host starts a session
- **THEN** an ASCII `dmon` banner and a tagline MOTD are appended to scrollback via `ITerminal.Scrollback.Append` before the first prompt is shown, and no bare `dmon` separator is used in their place

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

The terminal host SHALL re-render the completed turn with full Markdig markdown styling when `TurnEndEvent` is received, by producing a settled `IReadOnlyList<Line>` from the markdown source via a pure markdown-to-`Line[]` transform and appending those lines via `ITerminal.Scrollback.Append`. The transform SHALL compound inline styles for nested emphasis and SHALL recurse through link-label children rather than reading only the first literal child.

#### Scenario: Fenced code block rendered with border

- **WHEN** a completed turn contains a fenced code block
- **THEN** the settled render presents the code block with the conventional styled border (background tint or border characters), produced as a sequence of `Line`s from the markdown transform

#### Scenario: Settled lines replace the live block

- **WHEN** the live streamed block has been committed and the settled markdown render begins
- **THEN** the settled lines are appended to the scrollback in order, succeeding the streamed (now-committed) tokens

#### Scenario: Markdown transform is a pure function

- **WHEN** a unit test invokes the markdown renderer with a markdown source string
- **THEN** the renderer returns an `IReadOnlyList<Line>` deterministically, with no I/O, no `ITerminal` reference, and no global console state

#### Scenario: Nested emphasis compounds Format flags

- **WHEN** the markdown source contains nested emphasis (e.g. `**bold *and italic***`)
- **THEN** segments inside the inner emphasis SHALL render with BOTH the outer and inner Format flags set (`Format.Bold | Format.Italic`), produced by OR-ing the outer emphasis's Format with each inner segment's existing Style.Format rather than replacing the inner Style

#### Scenario: Link with rich-text label preserves label styling

- **WHEN** the markdown source contains a link whose label uses inline styling (e.g. `[**bold label**](https://example.com)`)
- **THEN** the rendered link segments SHALL carry BOTH the link's Style (underline + blue Foreground) AND the label's inner Format flags (e.g. `Format.Bold | Format.Underline`), produced by recursing through the `LinkInline` as a container of inlines rather than reading only `LinkInline.FirstChild` as a literal

#### Scenario: Link with empty label falls back to URL text

- **WHEN** the markdown source contains a link with no visible label (e.g. `[](https://example.com)`)
- **THEN** the rendered link segment SHALL display the URL string as the visible text with the link Style applied, preserving the prior renderer's empty-label fallback

### Requirement: Input prompt with horizontal separators

The terminal host SHALL present the input zone as a symmetric frame built from `dcli`'s three fixed-region bands: a `── dmon ──` rule pinned directly **above** the input editor via `ITerminal.InputPreamble.SetRows`; a `❯ ` prompt prefix on the editor's first row via `ITerminal.Input.SetPrompt`; and **below** the editor a horizontal `────` rule plus a readiness row `[Ready] dmon core v{version} {model}`, both set via `ITerminal.Status.SetRows`. The input editor's cursor and editing SHALL remain owned by `dcli` via `ITerminal.Input`. The readiness row SHALL show the active model name (not the protocol version) together with the `Thinking…`/`Idle` state indicator; `{version}` SHALL be the value reported in `agentReady.coreVersion`.

#### Scenario: Symmetric frame displayed when idle

- **WHEN** no turn is active
- **THEN** the input preamble shows a `── dmon ──` rule above the editor, the `dcli` input editor shows a `❯ ` prompt prefix with its cursor, and the status region below shows a `────` rule row and a `[Ready] dmon core v{version} {model}` readiness row

#### Scenario: Prompt glyph on the input line

- **WHEN** the input zone is rendered
- **THEN** the editor's first row is prefixed with `❯ `, set once via `Input.SetPrompt`, and the prefix persists across turns without emitting an `InputChanged` event

#### Scenario: Readiness row shows version and model

- **WHEN** the active model name is known and the core version from `agentReady` has been received
- **THEN** the bottom status row reads `[Ready] dmon core v{version} {model}` with the `Thinking…` or `Idle` indicator, set via `Status.SetRows`, and does not display the protocol version

### Requirement: Ctrl+C exits cleanly

The terminal host SHALL handle Ctrl+C via `dcli`'s `Events.KeyPressed` event (which surfaces `KeyEvent(Char('c'), Modifiers.Ctrl)` per `dcli`'s key-encoding contract — `dcli` does not terminate the process for the consumer) and SHALL perform a graceful shutdown: render the goodbye separator to scrollback *before* dcli's fixed region tears down, stop the core process, cancel background tasks, exit with code 0.

#### Scenario: Ctrl+C during idle

- **WHEN** the user presses Ctrl+C while no turn is active and `dcli` emits `KeyPressed(Char('c'), Ctrl)`
- **THEN** the host stops the core process and exits

#### Scenario: Ctrl+C during streaming

- **WHEN** the user presses Ctrl+C while a turn is streaming and `dcli` emits `KeyPressed(Char('c'), Ctrl)`
- **THEN** the host cancels the active turn's `CancellationToken`, stops the core process, and exits

#### Scenario: Goodbye separator rendered before dcli teardown

- **WHEN** the host enters the graceful-shutdown path (Ctrl+C, the legacy `Console.CancelKeyPress` redundancy net, or the core process exiting unexpectedly)
- **THEN** `renderer.PrintSeparator("goodbye")` runs while dcli's `ITerminal` is still alive (i.e. before the `await using ITerminal terminal` scope exits), so the user sees the goodbye separator in their terminal scrollback after the app exits

### Requirement: OS copy/paste works normally

The terminal host SHALL rely on `dcli`'s inline rendering (which preserves the terminal's native scrollback per `dcli`'s Decision 1) so the operating system's native text-selection and copy/paste functionality remains available. The host SHALL NOT enable an alternate-screen buffer.

#### Scenario: Text selection in terminal

- **WHEN** the user drags to select output text in their terminal emulator
- **THEN** the selection is handled by the terminal emulator; `dcli` does not own the alternate screen, so committed scrollback is fully selectable and copyable

### Requirement: Inline wizard prompts

The terminal host SHALL present provider setup and `/add-provider` wizard steps via `dcli`'s awaitable dialog methods (`await ITerminal.SelectAsync` for list pickers, `await ITerminal.InputAsync` for free-text and secret inputs). The wizard step order SHALL be: (1) Select Adapter, (2) Auth Configuration, (3) Select Model. The model selection step SHALL call `IProviderFactory.GetAvailableModelsAsync` with the API key resolved from the env var entered in step 2. If the live fetch fails or returns an empty list, the step SHALL fall back to the factory's static model list. A brief `Fetching models…` status SHALL be shown via `Status.SetRows` while the fetch is in progress. Multi-step pickers (adapter, model) SHALL be opened with `SelectRequest { AllowBack = true }` so the user can navigate back via Backspace. Free-text and secret steps (auth configuration) SHALL be opened with `InputRequest { AllowBack = true }` so the user can navigate back via Backspace pressed while the input field is empty.

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

- **WHEN** the user presses Backspace at a selection wizard step opened with `AllowBack = true` and before moving the selection
- **THEN** the dialog returns `DialogOutcome.Back` and the wizard returns to the previous step (using the existing back-stack)

#### Scenario: Back navigation from a text-input step via Backspace on empty

- **WHEN** the user presses Backspace at a free-text or secret wizard step opened with `InputRequest { AllowBack = true }` while the input field is empty
- **THEN** the dialog returns `DialogOutcome.Back` and the wizard returns to the previous step (using the existing back-stack)

#### Scenario: Wizard cancellation via Escape

- **WHEN** the user presses Escape during a wizard step
- **THEN** the dialog returns `DialogOutcome.Cancelled`, the wizard is cancelled, a notice is shown, and the input prompt is restored

### Requirement: WizardState carries a transient resolved API key
`Dmon.Terminal`'s `WizardState` SHALL include a `string? ResolvedApiKey` property. The auth configuration step (step 2) SHALL resolve the actual API key value from the environment variable name the user entered and store it in `ResolvedApiKey`. This field SHALL NOT be written to any config file or persistent store; it exists only in-memory for the duration of the wizard run.

#### Scenario: ResolvedApiKey set after auth step
- **WHEN** the user completes the auth configuration step with an env var name that is set in the environment
- **THEN** `WizardState.ResolvedApiKey` contains the value of that env var

#### Scenario: ResolvedApiKey null when env var not set
- **WHEN** the env var name the user entered is not set in the environment
- **THEN** `WizardState.ResolvedApiKey` is null and the model step falls back to the static list

#### Scenario: ResolvedApiKey not persisted
- **WHEN** the wizard completes and the provider config is written
- **THEN** the written config file contains no `ResolvedApiKey` field

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

### Requirement: /reload restarts the core to re-read config
The terminal host SHALL provide a `/reload` command that restarts the core process via `CoreProcessManager.RestartAsync`: stop the current core, spawn a fresh one, re-bind the host's stdio read/write loop to the new process's standard output/input, and re-open the active session directory on the fresh process (re-acquiring that directory's session lock). The fresh core re-reads `config.yaml` and loads the effective extension set. `/reload` SHALL run only between turns, never during an active streaming call.

Rehydrating the running conversation's message history into the fresh process is OUT OF SCOPE for this change: the core does not yet persist the active turn history to the session's `messages.jsonl` nor rehydrate `TurnHandler` state on `session.load`. The restart re-binds to the session directory; restoring prior conversation history across a restart is deferred to a follow-up change (it requires core/agent-core turn-persistence work).

#### Scenario: /reload spawns a fresh core and re-binds stdio
- **WHEN** the user issues `/reload` while idle
- **THEN** the previous core process is stopped and a new one is started
- **AND** the host reads subsequent events from the new process's standard output and writes commands to its standard input

#### Scenario: Active session directory is re-opened after /reload
- **WHEN** `/reload` is issued while a session is active
- **THEN** the new core re-opens the same session directory
- **AND** the fresh process holds that directory's session lock

#### Scenario: Config changes take effect after /reload
- **WHEN** the user adds or removes an extension in `config.yaml` and then issues `/reload`
- **THEN** the effective extension set on the fresh core reflects the edited config

#### Scenario: /reload is rejected during streaming
- **WHEN** `/reload` is issued during an active streaming turn
- **THEN** the restart does not occur until the turn completes

### Requirement: Terminal substrate is `dcli`

The terminal host SHALL use the `dcli` library's `ITerminal` façade (and `Dcli.Testing.HeadlessTerminal` for tests) as the substrate for all rendering, input handling, dialog presentation, and status display. The terminal host SHALL NOT depend on `Spectre.Console` and SHALL NOT call `System.Console.ReadKey` / `Console.Write` / `Console.SetCursorPosition` directly in any code that renders or accepts user input.

The mapping of terminal-host concerns to `dcli` surfaces SHALL be:

- Streaming token output → `ITerminal.Scrollback.BeginLive` + `AppendText` + `Commit`.
- Settled markdown render → `ITerminal.Scrollback.Append(Line)` with lines produced by the markdown renderer.
- Startup welcome banner + tagline MOTD → `ITerminal.Scrollback.Append(Line)`.
- Top input-frame rule (`── dmon ──`) → `ITerminal.InputPreamble.SetRows`.
- Input prompt prefix (`❯ `) → `ITerminal.Input.SetPrompt`.
- Horizontal separator lines → `ITerminal.Scrollback.Append(Line)` with the rule glyph composed inline (until a future `Scrollback.AppendRule` surfaces in `dcli`).
- Status / readiness row (model name, core version, working/idle state) → `ITerminal.Status.SetRows`.
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
- **THEN** every command the renderer issues (scrollback append, status set, input-preamble set, prompt set, dialog open) is recorded on the fake and assertable, without spinning a real terminal or a real render loop

#### Scenario: Headless harness drives integration tests

- **WHEN** an integration test uses `Dcli.Testing.HeadlessTerminal` to host the renderer
- **THEN** the real `dcli` render loop, layout, and overlay routing run against the test's scripted input and produce assertable frame snapshots

#### Scenario: Locked input dropped during a turn

- **WHEN** the terminal host's `IsLocked` flag is `true` and `ITerminal.Events` emits an `InputSubmitted` event
- **THEN** the host drops the submission, does not forward it to the core, and optionally surfaces a "still working" indicator via `Status.SetRows`

### Requirement: Host lifecycle hardening

The terminal host SHALL be defensive about three lifecycle concerns flagged during the `dmon-migration` reviewer audits: (1) `/reload` SHALL be single-shot under rapid-fire user input — the session loop SHALL recreate its restart-signal `TaskCompletionSource` *after* `CoreProcessManager.RestartAsync()` returns, not before, so a second `/reload` arriving during the restart window cannot trigger a benign double-restart; (2) `InputStateLayer.IsLocked` SHALL be cross-task visible — its backing field SHALL be declared `volatile bool` (or the equivalent memory-barrier guard) so a write on the RPC-dispatch task is observable to a subsequent read on the UI-dispatch task without reordering; (3) `ConsoleEventHandler.DrainAsync` SHALL handle non-cancellation exceptions gracefully — non-`OperationCanceledException` exceptions thrown out of `ChannelReader.ReadAllAsync` or the per-event dispatch SHALL be logged to the scrollback (one `[Drain Error] {type}: {message}` line) and SHALL trigger `CancellationTokenSource.Cancel()` on the host's outer CTS, so the session loop tears down cleanly rather than faulting `Task.WhenAll`.

#### Scenario: Rapid /reload single-shot

- **WHEN** the user submits `/reload` once via `dcli`'s `InputSubmitted` event and then submits `/reload` again while `CoreProcessManager.RestartAsync()` is still in flight
- **THEN** the host restarts the core process exactly once (not twice) and emits exactly one `[Reload] Core restarted.` system line

#### Scenario: IsLocked cross-task visibility

- **WHEN** `HandleRpcEventAsync` writes `_input.IsLocked = true` on `TurnStartEvent` (session-loop task) and `HandleUiEventAsync` reads `_input.IsLocked` on a subsequent `InputSubmitted` event (DrainAsync task)
- **THEN** the read on the UI task observes the write from the RPC task (no reordering or staleness across the task boundary)

#### Scenario: DrainAsync non-cancellation exception logged and cancelled

- **WHEN** `ConsoleEventHandler.DrainAsync` observes a non-`OperationCanceledException` exception from the channel reader or per-event dispatch
- **THEN** the host appends one `[Drain Error] {type}: {message}` line to the scrollback, calls `Cancel()` on the host's `CancellationTokenSource`, returns from `DrainAsync` normally (does not rethrow), and the outer session loop's `Task.WhenAll` completes successfully

### Requirement: Hardening behaviors verified against a live core

The terminal host's two user-visible hardening behaviors SHALL be confirmed against a live `Dmon.Core` process — not only via the tier-A/tier-B proxy tests, which approximate the `Program.cs` orchestration because the host exposes no injection seam. The live acceptance is: (1) the goodbye separator renders on Ctrl+C exit while `dcli`'s fixed region is still alive; (2) rapid-fire `/reload` submitted during the restart window restarts the core exactly once. This acceptance is a precondition for treating the hardening as field-verified; it is gated on `Dmon.Core` starting cleanly and a provider being configured per ADR-005.

#### Scenario: Goodbye separator visible on live Ctrl+C

- **WHEN** `dmon` is launched against a live core with a configured provider, the user interacts, and then presses Ctrl+C
- **THEN** the `── goodbye ──` separator appears in the terminal scrollback before the process exits with code 0

#### Scenario: Rapid /reload restarts once in a live session

- **WHEN** the user submits `/reload` twice in quick succession during the restart window in a live session
- **THEN** exactly one `[Reload] Core restarted.` system line appears (the core is restarted once, not twice)

