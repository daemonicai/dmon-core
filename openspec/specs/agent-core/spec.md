## Purpose

Define how the dmon agent core executes turns — running as an isolated JSONL/stdio process that drives the active `IChatClient` turn loop with tool invocation through the permission gate, message queuing, context compaction, transient-error retry, and thinking-level control — so hosts can submit turns and observe streamed results without embedding the agent loop.
## Requirements
### Requirement: Agent runs as an isolated process
The agent core SHALL run as a standalone process, communicating with host frontends exclusively via JSONL-over-stdio (ADR-003). No in-process embedding of the agent loop by hosts is supported.

#### Scenario: Core starts and signals readiness
- **WHEN** the agent core process is launched
- **THEN** it emits an `agentReady` event with `{protocolVersion, coreVersion}` on stdout before processing any commands

#### Scenario: Session is locked while open
- **WHEN** a core opens a session
- **THEN** it acquires an exclusive advisory lock on `<session-id>/.lock` (POSIX `flock` / Windows `LockFileEx`) for the duration of the session

#### Scenario: Second core fails to open a locked session
- **WHEN** a second core attempts to open a session that is already locked by another core
- **THEN** the second core releases its handle, emits `error {code: "sessionLocked", recoverable: false}`, and exits non-zero

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

### Requirement: Message queuing
The agent core SHALL support `turn.steer` (queued after current tool execution) and `turn.followUp` (queued after the agent finishes) to allow the host to redirect or extend the agent mid-turn without losing the current operation.

#### Scenario: Steer during tool execution
- **WHEN** the host sends `turn.steer` while a tool is executing
- **THEN** the steer message is delivered to the LLM after the current tool result is appended

#### Scenario: Follow-up after turn completes
- **WHEN** the host sends `turn.followUp`
- **THEN** the message is queued and submitted as a new turn after `agentEnd`

### Requirement: Context compaction
The agent core SHALL compact the session context when token usage approaches the model's context window limit, appending a non-destructive `CompactionMessage` to the session (ADR-004).

#### Scenario: Automatic compaction on threshold
- **WHEN** context usage exceeds the configured compaction threshold
- **THEN** the core emits `compactionStart`, summarises prior messages, appends a `CompactionMessage`, and emits `compactionEnd`

#### Scenario: Manual compaction
- **WHEN** the host sends `session.compact`
- **THEN** compaction is triggered immediately regardless of current token usage

### Requirement: Faithful session resume
On `session.load`, the agent core SHALL rehydrate the turn loop's conversation history from the canonical record so a freshly started core resumes with full tool-call and tool-result context, not text alone. The core SHALL read the canonical log, apply the compaction rule (records with `entryId` ≤ `supersedesUpTo` are skipped; the summary seeds the effective context start), and reconstruct in-memory `ChatMessage` history from the **replay subset** of parts: `text`, `toolCall`, `toolResult`, and `image`. `reasoning`, `usage`, and `unknown` parts SHALL NOT be replayed into LLM context. Reconstruction SHALL use dmon-owned record types mapped to `ChatMessage`; no third-party type appears in the persisted record.

#### Scenario: Resume restores tool context
- **WHEN** a session containing prior tool calls and results is loaded in a fresh core
- **THEN** the reconstructed history includes those tool calls and results (not just their text), so the next turn runs with faithful context

#### Scenario: Non-replayable parts are excluded from context
- **WHEN** the canonical record contains `reasoning`, `usage`, or `unknown` parts
- **THEN** they are preserved in the record but are not reconstructed into the LLM context on resume

#### Scenario: Compaction is honoured on resume
- **WHEN** the canonical record contains a `CompactionMessage`
- **THEN** records with `entryId` ≤ `supersedesUpTo` are skipped and the summary seeds the effective context start

### Requirement: State queries
The agent core SHALL respond to state query commands with current session and agent state, as typed events correlated by the originating command `id`.

#### Scenario: Get state
- **WHEN** the host sends `session.getStats` with `id` `"req-1"`
- **THEN** the core emits a `session.getStatsResult` event with `id` = `"req-1"` carrying a typed `SessionStats` payload

#### Scenario: Get messages
- **WHEN** the host sends `session.getMessages` with `id` `"req-2"`
- **THEN** the core emits a typed `SessionMessagesResultEvent` (`session.getMessagesResult`) with `id` = `"req-2"` carrying the session's full history as dmon-owned `message`/`compaction` records (the parts model), with no third-party type in the payload

### Requirement: Transient-error retry
The agent core SHALL retry provider calls that fail with retryable errors (HTTP 5xx, 429 rate-limit, provider-specific `overloaded`) using exponential backoff with jitter. Defaults — `baseDelay: 1s`, `maxDelay: 30s`, `maxAttempts: 5` — are overridable via `IConfiguration` under `Daemon:Provider:Retry:*`. The core SHALL honour `Retry-After` response headers when present.

#### Scenario: Retry attempt emits progress event
- **WHEN** the core retries a transient failure
- **THEN** it emits `retryAttempt {attempt, maxAttempts, nextDelayMs, reason}` before sleeping

#### Scenario: Non-retryable error fails the turn
- **WHEN** the core receives a 4xx response other than 408 or 429
- **THEN** the turn ends with `turnEnd {stopReason: error}` without retry

#### Scenario: Max attempts exhausted
- **WHEN** the core has retried `maxAttempts` times without success
- **THEN** the turn ends with `turnEnd {stopReason: error}` and the final error is reported

### Requirement: Thinking level abstraction
The agent core SHALL accept `thinking.set {level}` where `level ∈ {off, low, medium, high}` and SHALL map the level to the active provider's native reasoning parameter. `thinking.cycle` SHALL advance through the four levels in order.

#### Scenario: Thinking level mapped per provider
- **WHEN** the host sends `thinking.set {level: "high"}` and the active provider is Anthropic
- **THEN** subsequent LLM calls include the provider's native reasoning parameter at the value corresponding to `high`

#### Scenario: Capability ignored on incapable provider
- **WHEN** the host sends `thinking.set` and the active model does not support reasoning
- **THEN** the core emits `capabilityIgnored {capability: "thinking", requestedValue, reason}` and proceeds without the parameter

### Requirement: Provider SDK ABI consistency

The agent core's resolved runtime dependency closure SHALL be binary-compatible with the active provider SDK, such that a turn can be initiated against the configured provider without a runtime `MissingMethodException` / `MethodAccessException`. Specifically, the `Microsoft.Extensions.AI` family version SHALL match the `Microsoft.Extensions.AI.Abstractions` version that the bundled Anthropic provider SDK was compiled against. This version pin SHALL be documented at the dependency declaration so it is not bumped without re-validating provider SDK compatibility.

#### Scenario: First Anthropic turn does not fault on a missing SDK member

- **WHEN** the core executes the first `turn.submit` of a session with the Anthropic provider active
- **THEN** the provider call completes the turn-execution loop (emitting `turnStart` … `turnEnd`) without throwing a runtime `MissingMethodException` originating from a `Microsoft.Extensions.AI` type version mismatch

#### Scenario: M.E.AI version stays aligned with the Anthropic SDK

- **WHEN** the solution is built
- **THEN** the resolved `Microsoft.Extensions.AI.Abstractions` version equals the version declared by the bundled Anthropic provider SDK package, so no member referenced by the SDK is absent at runtime

### Requirement: Turn specifies the active model id

On each provider call, the agent core SHALL set `ChatOptions.ModelId` to the active model id before invoking the chat pipeline, so model resolution does not depend on a provider-specific baked-in default. The active model id SHALL be the registry's current model (`IProviderRegistry.GetCurrentModelId()`), falling back to the active provider's configured default model. When no model id is resolvable (both null or empty), the core SHALL leave `ChatOptions.ModelId` unset and rely on the provider client's default.

#### Scenario: Model id set on each turn

- **WHEN** the core executes a turn against the active provider
- **THEN** the `ChatOptions` passed to the provider call has `ModelId` set to the active model id, so providers whose adapter requires an explicit model (e.g. Gemini) complete the turn without throwing `Model ID must be specified`

#### Scenario: In-session model switch honoured on the next turn

- **WHEN** the host switches the model mid-session and then submits a new turn
- **THEN** the `ChatOptions.ModelId` for that turn reflects the switched-to model (`GetCurrentModelId()`), not only the static configured default

#### Scenario: No model configured leaves ModelId unset

- **WHEN** neither the registry's current model nor the configured default model resolves to a non-empty value
- **THEN** the core leaves `ChatOptions.ModelId` unset, preserving the provider client's baked-in default behaviour

### Requirement: Pending provider/model switch is committed before the turn runs

The agent core SHALL commit any pending provider/model switch at the start of a turn, before the active provider's `IChatClient` is resolved for that turn. As a result, a switch queued between turns SHALL take effect on the next turn (the turn uses the newly selected provider and model), while a switch queued during an in-flight turn SHALL defer to the following turn. When a switch is committed, the core SHALL emit `providerSwitched` with `effectiveNextTurn` reflecting whether the switch applies to the turn now starting (`false`) or a later turn.

#### Scenario: Between-turns selection used on the next turn

- **WHEN** the host sends `model.set` (provider and/or model) while no turn is running, and then submits a turn
- **THEN** the submitted turn resolves and calls the newly selected provider's client — not the previously active provider — because the pending switch is committed before the provider client is resolved

#### Scenario: Mid-turn switch still defers

- **WHEN** the host sends `model.set` while a turn is in flight
- **THEN** the in-flight turn completes on the previous provider, and the queued switch is committed at the start of the next turn and emitted there as `providerSwitched {..., effectiveNextTurn: false}` (no `providerSwitched` is emitted while the turn is still in flight)

#### Scenario: No pending switch leaves the active provider unchanged

- **WHEN** a turn starts and no provider/model switch is pending
- **THEN** the turn resolves the already-active provider client and no `providerSwitched` event is emitted

### Requirement: Dispatch loop does not block on long-running commands
The agent core's command dispatch SHALL NOT block the stdin reader on a command that suspends awaiting a later host command. Long-running interactive commands — `turn.submit` and `wizard.start` — SHALL be dispatched on a tracked background task so the reader continues consuming stdin and can route the commands that resolve a suspended operation (`tool.confirmResponse`, `ui.inputResponse`, `wizard.answer`, `turn.abort`). Short, non-interactive commands SHALL continue to be processed inline. This conforms to ADR-003's "commands are fire-and-forget; the core suspends the turn until the response arrives". Errors raised by a backgrounded command SHALL be surfaced as an `error` event and SHALL NOT be silently dropped, and outstanding background tasks SHALL be observable at shutdown.

#### Scenario: Tool-confirmation round-trip completes over the real loop
- **WHEN** the host sends `turn.submit`, the core emits `tool.confirmRequest`, and the host then sends `tool.confirmResponse` as a subsequent line
- **THEN** the reader reads and routes the `tool.confirmResponse` while the turn is suspended, the turn resumes, and `turnEnd` is emitted (no deadlock)

#### Scenario: Wizard round-trip completes over the real loop
- **WHEN** the host sends `wizard.start`, the core emits a `wizard.step` event, and the host then sends `wizard.answer` as a subsequent line
- **THEN** the reader reads and routes the `wizard.answer` while the wizard is suspended, the wizard advances, and a further `wizard.step` or `providerConfigured` event is emitted

#### Scenario: Reader stays responsive during a long turn
- **WHEN** a `turn.submit` is in progress and the host sends `turn.abort`
- **THEN** the reader reads and routes `turn.abort` without waiting for the turn to finish, and the turn is cancelled

#### Scenario: Backgrounded command error is surfaced
- **WHEN** a backgrounded long-running command throws
- **THEN** the core emits an `error` event describing the failure rather than dropping it silently

### Requirement: Malformed and unrecognized commands are rejected without killing the reader
The agent core's command dispatch SHALL treat malformed input, structurally invalid commands, and unrecognized command types as recoverable, per-line failures: for each such line it SHALL emit an `error` event and continue reading subsequent lines. A single bad command line SHALL NOT terminate the dispatch loop, drop the line silently, or prevent later valid commands from being processed. The dispatch loop SHALL remain a single sequential reader. Specifically:

- A line that is not parseable JSON SHALL produce `error {code: "malformedCommand", recoverable: true}`.
- A JSON object with no `type` field SHALL produce `error {code: "missingType", recoverable: true}`.
- A JSON object whose `type` does not correspond to a known command, or whose payload cannot be bound to that command type, SHALL produce `error {code: "unknownCommand", recoverable: true}`.
- A command whose handler raises a not-implemented condition SHALL produce `error {code: "notImplemented", recoverable: true}`.
- A command whose handler raises any other unexpected error SHALL produce `error {code: "internalError", recoverable: false}`.

Command routing SHALL be driven by the command type's polymorphic discriminator (the single `Command` type-discriminator table), not by a separately maintained mapping.

#### Scenario: Malformed JSON line does not stop the loop
- **WHEN** the host sends a line that is not valid JSON, then sends a valid command on the next line
- **THEN** the core emits `error {code: "malformedCommand", recoverable: true}` for the bad line and still processes the following valid command

#### Scenario: Command missing the type field
- **WHEN** the host sends a JSON object that has no `type` field
- **THEN** the core emits `error {code: "missingType", recoverable: true}` and continues reading

#### Scenario: Unknown command type
- **WHEN** the host sends a JSON object whose `type` is not a recognized command
- **THEN** the core emits `error {code: "unknownCommand", recoverable: true}` and continues reading

#### Scenario: Handler failure is surfaced as a non-recoverable error
- **WHEN** a recognized command's handler raises an unexpected exception
- **THEN** the core emits `error {code: "internalError", recoverable: false}` rather than terminating the reader, and the reader continues processing subsequent commands

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

