## MODIFIED Requirements

### Requirement: Console host is a thin RPC client
The console host SHALL communicate with the agent core exclusively via the JSONL-over-stdio RPC protocol (ADR-003). No agent logic, session management, or tool execution SHALL be implemented in the host process. The host SHALL be implemented using Terminal.Gui v2 for layout, input, and rendering.

#### Scenario: Host spawns core process on startup
- **WHEN** the user launches `daemon` from the terminal
- **THEN** the console host spawns the agent core process and connects via stdio

#### Scenario: Host receives and renders streaming events
- **WHEN** the core emits `messageDelta` events
- **THEN** the host appends the token to the current output block and redraws the view in real time

### Requirement: Input field is locked during an active turn
The host's input field SHALL be disabled while a turn is active, preventing the user from submitting a new message before the current turn completes.

#### Scenario: Input disabled on turn start
- **WHEN** the host receives a `TurnStartEvent`
- **THEN** the input `TextField` is disabled and visually indicates it is locked

#### Scenario: Input re-enabled on turn end
- **WHEN** the host receives a `TurnEndEvent`
- **THEN** the input `TextField` is re-enabled and focus is returned to it

### Requirement: Tool confirmation UI
The console host SHALL present tool confirmation prompts as Terminal.Gui modal dialogs and relay the response to the core.

#### Scenario: Confirmation dialog displayed
- **WHEN** the core emits `tool.confirmRequest {name, args, risk}`
- **THEN** the host displays a modal dialog with the tool name, arguments, and risk level, offering Allow once / Allow for project / Allow globally / Deny

#### Scenario: High-risk confirmation visually distinct
- **WHEN** the confirmation dialog has `risk: high`
- **THEN** the dialog renders with a visual indicator distinguishing it from low-risk prompts

#### Scenario: User response relayed to core
- **WHEN** the user selects an option from the confirmation dialog
- **THEN** the host sends `tool.confirmResponse {id, confirmed}` or `{id, cancelled}` to the core

### Requirement: Setup wizard is a composable step-dialog sequence
The setup wizard SHALL be implemented as an ordered list of dialog steps, each receiving and returning a `WizardState` record. The runner SHALL support Back navigation by maintaining a stack of prior states.

#### Scenario: Provider selection step shown
- **WHEN** the wizard is launched
- **THEN** the first dialog presents a list of available adapters for the user to choose from

#### Scenario: Model selection step follows adapter selection
- **WHEN** the user selects an adapter and advances
- **THEN** the next dialog presents the available models for that adapter

#### Scenario: Auth configuration step follows model selection
- **WHEN** the user selects a model and advances
- **THEN** the next dialog presents auth configuration options (environment variable name or direct entry)

#### Scenario: Back navigation returns to previous step
- **WHEN** the user presses Back on any step after the first
- **THEN** the previous step dialog is displayed with the state from that point restored

#### Scenario: Cancelling any step aborts the wizard
- **WHEN** the user cancels any step dialog
- **THEN** the wizard exits without sending a configure command to the core

### Requirement: Status bar shows agent state
The host SHALL display a status bar showing the active model name and the current agent state (Thinking while a turn is active, Idle otherwise).

#### Scenario: Status shows Thinking during turn
- **WHEN** a turn is active
- **THEN** the status bar displays a visual indicator and the text "Thinking"

#### Scenario: Status shows Idle between turns
- **WHEN** no turn is active
- **THEN** the status bar displays the active model name and "Idle"

### Requirement: User input and slash commands
The console host SHALL provide an input field for user messages and SHALL handle slash commands locally before forwarding or acting on them.

#### Scenario: User message submitted
- **WHEN** the user types a message and presses Enter
- **THEN** the host sends `turn.submit {message}` to the core

#### Scenario: Slash command handled
- **WHEN** the user types `/login anthropic`
- **THEN** the host sends `auth.login {provider: "anthropic"}` to the core and manages the resulting UI interaction flow

#### Scenario: Unknown slash command reported
- **WHEN** the user types an unrecognised slash command
- **THEN** the host displays an error message without forwarding to the core

### Requirement: Session management commands
The console host SHALL expose session management via slash commands.

#### Scenario: New session
- **WHEN** the user types `/new`
- **THEN** the host sends `session.create` and displays the new session context

#### Scenario: Fork session
- **WHEN** the user types `/fork`
- **THEN** the host sends `session.fork` with the current last `entryId` and switches to the new session

### Requirement: Provider switching
The console host SHALL expose provider switching via slash commands.

#### Scenario: Cycle provider with slash command
- **WHEN** the user types `/model` or the configured hotkey
- **THEN** the host sends `model.cycle` and displays the newly active provider and model name

#### Scenario: Set provider explicitly
- **WHEN** the user types `/model anthropic claude-sonnet-4-6`
- **THEN** the host sends `model.set {provider: "anthropic", modelId: "claude-sonnet-4-6"}`

### Requirement: UI input requests
The console host SHALL handle `ui.inputRequest` events as modal dialogs. Secret inputs (`kind: "secret"`) SHALL be masked.

#### Scenario: Secret input masked
- **WHEN** the core emits `ui.inputRequest {kind: "secret", prompt}`
- **THEN** the host displays a modal dialog with the prompt and reads input with character masking, then sends `ui.inputResponse {id, value}`

### Requirement: Bootstrap notice
The console host SHALL render `bootstrapNotice` events so the user is aware when `.daemon/` was auto-created.

#### Scenario: Bootstrap notice rendered
- **WHEN** the core emits `bootstrapNotice {path, created[]}`
- **THEN** the host displays a one-line notice naming the directory and listing the files created

### Requirement: Thinking level control
The console host SHALL expose thinking-level control via `/thinking <level>` and `/thinking` (cycle).

#### Scenario: Thinking level set
- **WHEN** the user types `/thinking high`
- **THEN** the host sends `thinking.set {level: "high"}`

#### Scenario: Thinking cycle
- **WHEN** the user types `/thinking` with no argument
- **THEN** the host sends `thinking.cycle`
