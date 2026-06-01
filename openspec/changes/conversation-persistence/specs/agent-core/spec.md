## ADDED Requirements

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

## MODIFIED Requirements

### Requirement: State queries
The agent core SHALL respond to state query commands with current session and agent state, as typed events correlated by the originating command `id`.

#### Scenario: Get state
- **WHEN** the host sends `session.getStats` with `id` `"req-1"`
- **THEN** the core emits a `session.getStatsResult` event with `id` = `"req-1"` carrying a typed `SessionStats` payload

#### Scenario: Get messages
- **WHEN** the host sends `session.getMessages` with `id` `"req-2"`
- **THEN** the core emits a typed `SessionMessagesResultEvent` (`session.getMessagesResult`) with `id` = `"req-2"` carrying the session's full history as dmon-owned `message`/`compaction` records (the parts model), with no third-party type in the payload
