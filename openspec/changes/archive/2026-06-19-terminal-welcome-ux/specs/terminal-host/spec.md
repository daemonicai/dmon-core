## ADDED Requirements

### Requirement: Startup welcome banner and MOTD

The terminal host SHALL, on startup, write an ASCII `dmon` banner and a tagline MOTD to scrollback via `ITerminal.Scrollback.Append`, replacing the prior bare `dmon` separator. The banner and tagline SHALL be emitted into scrollback (so they scroll away with history) rather than pinned in the fixed region, and SHALL appear before the first input prompt is presented.

#### Scenario: Banner and tagline shown at startup

- **WHEN** the terminal host starts a session
- **THEN** an ASCII `dmon` banner and a tagline MOTD are appended to scrollback via `ITerminal.Scrollback.Append` before the first prompt is shown, and no bare `dmon` separator is used in their place

## MODIFIED Requirements

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
