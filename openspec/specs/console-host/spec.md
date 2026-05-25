## ADDED Requirements

### Requirement: Console host is a thin RPC client
The console host SHALL communicate with the agent core exclusively via the JSONL-over-stdio RPC protocol (ADR-003). No agent logic, session management, or tool execution SHALL be implemented in the host process. The host SHALL use Spectre.Console for output rendering and a `Console.ReadKey` async loop for input; it SHALL NOT use Spectre.Console's synchronous prompt or `AnsiConsole.Prompt` APIs.

#### Scenario: Host spawns core process on startup
- **WHEN** the user launches `dmon` from the terminal
- **THEN** the terminal host spawns the agent core process and connects via stdio

#### Scenario: Host receives and renders streaming events
- **WHEN** the core emits `messageDelta` events
- **THEN** the host renders incremental text to the terminal in real time without blocking the input loop

### Requirement: User input and slash commands
The console host SHALL provide a prompt for user input and SHALL handle slash commands locally before forwarding or acting on them.

#### Scenario: User message submitted
- **WHEN** the user types a message and presses Enter
- **THEN** the host sends `turn.submit {message}` to the core

#### Scenario: Slash command handled
- **WHEN** the user types `/login anthropic`
- **THEN** the host sends `auth.login {provider: "anthropic"}` to the core and manages the resulting UI interaction flow

#### Scenario: Unknown slash command reported
- **WHEN** the user types an unrecognised slash command
- **THEN** the host displays an error message without forwarding to the core

### Requirement: Tool confirmation UI
The console host SHALL present tool confirmation prompts to the user and relay the response to the core.

#### Scenario: Confirmation prompt displayed
- **WHEN** the core emits `tool.confirmRequest {name, args, risk}`
- **THEN** the host displays the tool name, arguments, and risk level, and offers Allow once / Allow for project / Allow globally / Deny

#### Scenario: High-risk confirmation visually distinct
- **WHEN** the confirmation prompt has `risk: high`
- **THEN** the host renders the prompt with a visual indicator distinguishing it from low-risk prompts

#### Scenario: User response relayed to core
- **WHEN** the user selects an option from the confirmation prompt
- **THEN** the host sends `tool.confirmResponse {id, confirmed}` or `{id, cancelled}` to the core

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
The console host SHALL handle `ui.inputRequest` events distinctly from `tool.confirmRequest`. Secret inputs (`kind: "secret"`) SHALL be masked.

#### Scenario: Secret input masked
- **WHEN** the core emits `ui.inputRequest {kind: "secret", prompt}`
- **THEN** the host displays the prompt and reads input with character masking, then sends `ui.inputResponse {id, value}`

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
