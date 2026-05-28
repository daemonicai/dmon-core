## MODIFIED Requirements

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
