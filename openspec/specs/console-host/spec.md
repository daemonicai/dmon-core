## Purpose

The console host is the terminal-facing layer of dmon. It owns input, scrollback rendering, dialog surfaces, and status display, and communicates with the agent core exclusively via the JSONL-over-stdio RPC protocol (ADR-003).

## Requirements

### Requirement: Console host is a thin RPC client
The console host SHALL communicate with the agent core exclusively via the JSONL-over-stdio RPC protocol (ADR-003). No agent logic, session management, or tool execution SHALL be implemented in the host process. The host SHALL use Spectre.Console for output rendering and a `Console.ReadKey` async loop for input; it SHALL NOT use Spectre.Console's synchronous prompt or `AnsiConsole.Prompt` APIs.

#### Scenario: Host spawns core process on startup
- **WHEN** the user launches `dmon` from the terminal
- **THEN** the terminal host spawns the agent core process and connects via stdio

#### Scenario: Host receives and renders streaming events
- **WHEN** the core emits `messageDelta` events
- **THEN** the host renders incremental text to the terminal in real time without blocking the input loop

### Requirement: User input and slash commands

The console host SHALL receive user input by subscribing to `dcli`'s `ITerminal.Events` stream — specifically the `InputSubmitted(text)` event emitted when the user presses Enter — and SHALL handle slash commands locally (via the existing `SlashCommandParser`) before forwarding or acting on them. The console host SHALL NOT poll `Console.ReadKey` and SHALL NOT own a dedicated input thread; line editing, history, and grapheme-aware caret movement are `dcli`'s responsibility.

#### Scenario: User message submitted

- **WHEN** the user types a message and presses Enter and `dcli` emits `InputSubmitted(text)`
- **THEN** the host sends `turn.submit {message: text}` to the core

#### Scenario: Slash command handled

- **WHEN** `dcli` emits `InputSubmitted("/login anthropic")`
- **THEN** the host parses the slash command and sends `auth.login {provider: "anthropic"}` to the core, then manages the resulting UI interaction flow

#### Scenario: Unknown slash command reported

- **WHEN** `dcli` emits `InputSubmitted` for an unrecognised slash command
- **THEN** the host appends an "unknown command" message to the scrollback via `ITerminal.Scrollback.Append`

### Requirement: Tool confirmation UI

The console host SHALL present tool confirmation prompts via `dcli`'s `await ITerminal.ChoiceAsync` and SHALL relay the user's choice to the core via `tool.confirmResponse`.

#### Scenario: Confirmation prompt displayed

- **WHEN** the core emits `tool.confirmRequest {name, args, risk}`
- **THEN** the host opens a `ChoiceAsync` overlay showing the tool name, arguments, and risk level, offering Allow once / Allow for project / Allow globally / Deny

#### Scenario: High-risk confirmation visually distinct

- **WHEN** the confirmation prompt has `risk: high`
- **THEN** the `ChoiceRequest` prompt includes a styled visual indicator (red bold `⚠ HIGH RISK`) distinguishing it from low-risk prompts

#### Scenario: User response relayed to core

- **WHEN** the dialog returns `DialogResult<int>` with `Outcome = Submitted`
- **THEN** the host maps the chosen index to the appropriate `(confirmed, scope)` pair and sends `tool.confirmResponse`

### Requirement: Session management commands
The console host SHALL expose session management via slash commands.

#### Scenario: New session
- **WHEN** the user types `/new`
- **THEN** the host sends `session.create` and displays the new session context

#### Scenario: Fork session
- **WHEN** the user types `/fork`
- **THEN** the host sends `session.fork` with the current last `entryId` and switches to the new session

### Requirement: Provider switching

The console host SHALL expose provider switching via slash commands and, when the user invokes a non-targeted variant, via `dcli`'s `await ITerminal.SelectAsync` to pick a provider/model interactively.

#### Scenario: Cycle provider with slash command

- **WHEN** the user submits `/model` (no arguments)
- **THEN** the host sends `model.cycle` and displays the newly active provider and model name via `Status.SetRows`

#### Scenario: Set provider explicitly

- **WHEN** the user submits `/model anthropic claude-sonnet-4-6`
- **THEN** the host sends `model.set {provider: "anthropic", modelId: "claude-sonnet-4-6"}`

### Requirement: UI input requests

The console host SHALL handle `ui.inputRequest` events via `dcli`'s `await ITerminal.InputAsync` and SHALL distinguish them from `tool.confirmRequest`. For secret inputs (`kind: "secret"`), the host SHALL set `InputRequest.IsSecret = true` so `dcli` masks both the seeded default and live typing as bullets while returning the real string on submit.

#### Scenario: Secret input masked

- **WHEN** the core emits `ui.inputRequest {kind: "secret", prompt}`
- **THEN** the host opens an `InputAsync` overlay with `IsSecret = true`, the user sees bullets as they type (and as the default if any), and on submit the host sends `ui.inputResponse {id, value}` with the real value

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

