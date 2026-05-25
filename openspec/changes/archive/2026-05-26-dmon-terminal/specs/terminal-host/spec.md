## ADDED Requirements

### Requirement: Streaming output renders in real time
The terminal host SHALL display `messageDelta` tokens to stdout as they arrive without buffering. The input prompt SHALL remain responsive (accepting keystrokes, discarding them silently) while a turn is active.

#### Scenario: Tokens appear as they stream
- **WHEN** the core emits a sequence of `messageDelta` events during a turn
- **THEN** each token is appended to the current output line immediately, visible to the user without waiting for the turn to end

#### Scenario: Input discarded during streaming
- **WHEN** the user types while a turn is active
- **THEN** keystrokes are silently discarded and no partial input is shown or forwarded

### Requirement: Settled markdown render on turn end
The terminal host SHALL re-render the completed turn with full Markdig markdown styling when `TurnEndEvent` is received.

#### Scenario: Fenced code block rendered with border
- **WHEN** a completed turn contains a fenced code block
- **THEN** the settled output shows the code in a Spectre.Console panel with monospace styling

#### Scenario: Bullet list rendered with bullet prefix
- **WHEN** a completed turn contains a markdown list
- **THEN** each item is prefixed with `•` and indented

#### Scenario: Bold and italic rendered
- **WHEN** a completed turn contains `**bold**` or `_italic_` markdown
- **THEN** the text is rendered with the corresponding Spectre.Console style attribute

### Requirement: Input prompt with horizontal separators
The terminal host SHALL display the input zone as a ` > ` prompt between two `─────` horizontal separator lines.

#### Scenario: Prompt displayed when idle
- **WHEN** no turn is active
- **THEN** the terminal shows a `─────` separator line, then ` > ` with cursor, then a second `─────` separator

#### Scenario: Status shown in top separator
- **WHEN** the active model name is known
- **THEN** the top separator line includes the model name and `Thinking…` or `Idle` state indicator

### Requirement: Ctrl+C exits cleanly
The terminal host SHALL handle `SIGINT` (Ctrl+C) via `Console.CancelKeyPress` and perform a graceful shutdown: stop the core process, cancel background tasks, exit with code 0.

#### Scenario: Ctrl+C during idle
- **WHEN** the user presses Ctrl+C while no turn is active
- **THEN** the host stops the core process and exits

#### Scenario: Ctrl+C during streaming
- **WHEN** the user presses Ctrl+C while a turn is streaming
- **THEN** the host cancels the CancellationToken, stops the core process, and exits

### Requirement: OS copy/paste works normally
The terminal host SHALL NOT put the terminal into raw mouse-capture mode. Text selection and copy/paste SHALL function via the operating system's native terminal behaviour.

#### Scenario: Text selection in terminal
- **WHEN** the user drags to select output text in their terminal emulator
- **THEN** the selection is handled by the terminal emulator; the host does not interfere

### Requirement: Inline wizard prompts
The terminal host SHALL present provider setup and `/add-provider` wizard steps as numbered inline prompts rendered to stdout.

#### Scenario: Adapter selection prompt
- **WHEN** the wizard is at the adapter-selection step
- **THEN** the host prints a numbered list of available adapters and reads a single digit from the user

#### Scenario: Back navigation
- **WHEN** the user types `b` or `0` at a wizard step
- **THEN** the wizard returns to the previous step (using `WizardRunner`'s back-stack)

#### Scenario: Wizard cancellation
- **WHEN** the user presses Ctrl+C during a wizard step
- **THEN** the wizard is cancelled, a notice is shown, and the input prompt is restored

### Requirement: Inline tool confirmation prompt
The terminal host SHALL present `tool.confirmRequest` as a numbered inline prompt with the tool name, arguments, and risk level.

#### Scenario: Tool confirm prompt displayed
- **WHEN** the core emits `tool.confirmRequest`
- **THEN** the host prints the tool name, args, and risk level, with four numbered options: Allow once / Allow for project / Allow globally / Deny

#### Scenario: High-risk prompt visually distinct
- **WHEN** `risk` is `high`
- **THEN** the prompt includes a `[red]⚠ HIGH RISK[/]` warning line before the options

#### Scenario: Response relayed to core
- **WHEN** the user selects an option
- **THEN** the host sends `tool.confirmResponse` with the appropriate `confirmed` flag and `scope`
