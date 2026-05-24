## MODIFIED Requirements

### Requirement: Turn execution loop
The agent core SHALL execute turns by calling the active `IChatClient`, handling tool invocations via the permission gate, and streaming results back to the host as events. Before the first turn of each session, the core SHALL prepend the assembled system prompt (via `ISystemPromptBuilder`) as a `ChatRole.System` message at index 0 of the conversation history.

#### Scenario: Standard turn with no tool calls
- **WHEN** the host sends `turn.submit` with a user message
- **THEN** the core emits `turnStart`, one or more `messageDelta` events, then `turnEnd`

#### Scenario: Turn with tool calls
- **WHEN** the LLM response includes tool calls
- **THEN** the core emits `toolExecutionStart`, invokes the tool via the permission gate, emits `toolExecutionEnd`, and continues the turn

#### Scenario: Turn abort
- **WHEN** the host sends `turn.abort` during an active turn
- **THEN** the current LLM call and any pending tool invocations are cancelled and `turnEnd` is emitted with `stopReason: aborted`

#### Scenario: System message present on first turn
- **WHEN** the host sends the first `turn.submit` of a session
- **THEN** the LLM pipeline receives the history with the system message at index 0 followed by the user message
