## ADDED Requirements

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
The agent core SHALL execute turns by calling the active `IChatClient`, handling tool invocations via the permission gate, and streaming results back to the host as events.

#### Scenario: Standard turn with no tool calls
- **WHEN** the host sends `turn.submit` with a user message
- **THEN** the core emits `turnStart`, one or more `messageDelta` events, then `turnEnd`

#### Scenario: Turn with tool calls
- **WHEN** the LLM response includes tool calls
- **THEN** the core emits `toolExecutionStart`, invokes the tool via the permission gate, emits `toolExecutionEnd`, and continues the turn

#### Scenario: Turn abort
- **WHEN** the host sends `turn.abort` during an active turn
- **THEN** the current LLM call and any pending tool invocations are cancelled and `turnEnd` is emitted with `stopReason: aborted`

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
