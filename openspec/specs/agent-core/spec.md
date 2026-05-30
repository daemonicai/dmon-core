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

### Requirement: State queries
The agent core SHALL respond to state query commands with current session and agent state.

#### Scenario: Get state
- **WHEN** the host sends `session.getStats`
- **THEN** the core responds with token counts, cost, context usage, and current model

#### Scenario: Get messages
- **WHEN** the host sends `session.getMessages`
- **THEN** the core responds with the full message history for the current session

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

