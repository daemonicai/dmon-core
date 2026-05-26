## Requirements

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
The terminal host SHALL present provider setup and `/add-provider` wizard steps as numbered inline prompts rendered to stdout. The wizard step order SHALL be: (1) Select Adapter, (2) Auth Configuration, (3) Select Model. The model selection step SHALL call `IProviderFactory.GetAvailableModelsAsync` with the API key resolved from the env var entered in step 2. If the live fetch fails or returns an empty list, the step SHALL fall back to the factory's static model list. A brief `Fetching models…` status line SHALL be shown while the fetch is in progress. List prompts with more than one option SHALL use arrow-key navigation (↑/↓ to move, Enter to select) rendered in-place using ANSI cursor movement.

#### Scenario: Adapter selection prompt
- **WHEN** the wizard is at the adapter-selection step (step 1)
- **THEN** the host prints a scrollable list of available adapters with arrow-key navigation

#### Scenario: Auth config precedes model selection
- **WHEN** the user completes step 1 (adapter)
- **THEN** the wizard shows the auth configuration prompt (step 2) before the model selection prompt (step 3)

#### Scenario: Live model list shown when key resolves
- **WHEN** the user completes step 2 and the env var they entered is set in the environment
- **THEN** step 3 shows the live model list fetched from the provider, with `Fetching models…` displayed while the call is in flight

#### Scenario: Static fallback shown when fetch fails
- **WHEN** the live model fetch in step 3 fails for any reason
- **THEN** the wizard shows the static fallback model list and continues normally without surfacing an error

#### Scenario: Back navigation
- **WHEN** the user presses `b` or `0` at a wizard step
- **THEN** the wizard returns to the previous step (using `WizardRunner`'s back-stack)

#### Scenario: Wizard cancellation
- **WHEN** the user presses Ctrl+C or `q` during a wizard step
- **THEN** the wizard is cancelled, a notice is shown, and the input prompt is restored

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
