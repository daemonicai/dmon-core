## ADDED Requirements

### Requirement: Typed, correlated command results

The agent core SHALL respond to every command that produces a result with a **dedicated typed event** whose discriminator is `type` and whose fields are named and typed. There SHALL be no generic `{type:"response", command, success, data}` envelope and no `object`/`data:any` payload in the command-result surface (the sole, explicitly-bounded exception is `session.getMessages`, see "State queries").

Every command-result event SHALL derive from a `ResultEvent` base that carries the originating command's `id` (serialized as `id`), so each result correlates unambiguously to the command that produced it. Command **failures** SHALL be reported by a `CommandErrorEvent` (a `ResultEvent`) carrying `id`, `command`, `code`, and `message`. The existing `error` event SHALL be retained for **ambient** (non-command) core errors and SHALL NOT carry a command correlation id.

Streaming and notification events (`turnStart`, `messageStart`/`messageDelta`/`messageEnd`, `toolExecutionStart`/`toolExecutionEnd`, `sessionUpdated`, `compactionStart`/`compactionEnd`, `retryAttempt`, …) are not command results and SHALL remain without a correlation `id`. Server-initiated round-trips (`tool.confirmRequest`, `ui.inputRequest`) SHALL keep their own server-generated request id.

#### Scenario: Command result is a typed, correlated event
- **WHEN** the host sends a command with `id` `"req-1"` that produces a result (e.g. `session.create`, `session.list`, `model.list`, `auth.status`)
- **THEN** the core emits a dedicated typed event for that command (not a `{type:"response"}` envelope) whose `id` equals `"req-1"` and whose payload fields are named and typed

#### Scenario: Command failure is a correlated CommandErrorEvent
- **WHEN** the host sends a command with `id` `"req-2"` that fails (e.g. `session.fork` with no active session)
- **THEN** the core emits a `commandError` event with `id` = `"req-2"`, the `command` name, a `code`, and a `message`, and does NOT emit a `{success:false}` response envelope

#### Scenario: Ambient core error remains an uncorrelated error event
- **WHEN** a non-command core error occurs (e.g. an extension fails to load)
- **THEN** the core emits an `error` event with `code`, `message`, and `recoverable`, and that event carries no command correlation `id`

## MODIFIED Requirements

### Requirement: State queries
The agent core SHALL respond to state query commands with current session and agent state, as typed events correlated by the originating command `id` (per "Typed, correlated command results").

#### Scenario: Get state
- **WHEN** the host sends `session.getStats` with `id` `"req-3"`
- **THEN** the core emits a `session.getStatsResult` event with `id` = `"req-3"` carrying a typed `SessionStats` payload (`tokens`, `cost`, `contextUsage`, `currentModel`) — not an anonymous object inside a `data` field

#### Scenario: Get messages (deferred typing)
- **WHEN** the host sends `session.getMessages`
- **THEN** the core responds with the full message history for the current session; `session.getMessages` is the single command permitted to retain an untyped payload until the canonical turn-persistence change defines the conversation-message DTO, at which point it SHALL convert to a typed `session.getMessagesResult` event
