## MODIFIED Requirements

### Requirement: Turn execution loop
The agent core SHALL execute turns by calling the active `IChatClient`, handling tool invocations via the permission gate, and streaming results back to the host as events. Before the first turn of each session, the core SHALL prepend the assembled system prompt (via `ISystemPromptBuilder`) as a `ChatRole.System` message at index 0 of the conversation history. When a turn includes tool calls, the core SHALL record the structured tool calls and tool results into the conversation history — the assistant message carrying its text and `toolCall` content, followed by a tool-role message carrying the corresponding `toolResult` content — so that tool calls and results are persisted via session-storage and replayed on resume, not discarded after the turn. The core SHALL NOT append a text-only assistant message that omits the turn's tool calls. The `toolExecutionEnd` event SHALL carry the real tool result and its error state, correlated to the originating call by `callId`; the core SHALL NOT emit a placeholder result.

#### Scenario: Standard turn with no tool calls
- **WHEN** the host sends `turn.submit` with a user message
- **THEN** the core emits `turnStart`, one or more `messageDelta` events, then `turnEnd`

#### Scenario: Turn with tool calls
- **WHEN** the LLM response includes tool calls
- **THEN** the core emits `toolExecutionStart`, invokes the tool via the permission gate, emits `toolExecutionEnd` carrying the real tool result and `isError`, and continues the turn

#### Scenario: Tool calls and results are recorded in history
- **WHEN** a turn completes in which the LLM called one or more tools
- **THEN** the conversation history (and the persisted `messages.jsonl`) contains the assistant's `toolCall` parts and the corresponding `toolResult` parts as structured content, not only the final assistant text

#### Scenario: Recorded tool context is restored on resume
- **WHEN** a session whose history contains tool calls and results is loaded in a fresh core
- **THEN** the reconstructed history includes those tool calls and results, so the next turn runs with faithful tool context

#### Scenario: Tool result event reflects the real result
- **WHEN** a tool is invoked during a turn
- **THEN** the `toolExecutionEnd` event for that call carries the actual result value produced by the tool (not a placeholder) and the correct `isError` state

#### Scenario: Turn abort
- **WHEN** the host sends `turn.abort` during an active turn
- **THEN** the current LLM call and any pending tool invocations are cancelled and `turnEnd` is emitted with `stopReason: cancelled`

#### Scenario: System message present on first turn
- **WHEN** the host sends the first `turn.submit` of a session
- **THEN** the LLM pipeline receives the history with the system message at index 0 followed by the user message
</content>
