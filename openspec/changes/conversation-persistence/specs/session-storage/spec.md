## ADDED Requirements

### Requirement: Session-storage owns the canonical conversation record
Session-storage SHALL own the write of the canonical conversation log. At turn completion the orchestration point SHALL append the turn(s) to `messages.jsonl` via session-storage **before** the turns are handed to the memory tier for indexing. The memory tier SHALL NOT write `messages.jsonl`. Session-storage SHALL mint each record's `entryId` at append time and SHALL supply that `entryId` to the memory tier, so the derived index, `forkEntryId`, and compaction `supersedesUpTo` reference one id space.

#### Scenario: Canonical append precedes indexing
- **WHEN** a turn completes
- **THEN** session-storage appends the turn's record to `messages.jsonl` and mints its `entryId`, and only then are the turns passed to the memory tier for index/distillation

#### Scenario: entryId is owned by session-storage
- **WHEN** a turn is recorded
- **THEN** the `entryId` written to `messages.jsonl` is the same id the memory index keys on (the memory tier does not mint its own)

## MODIFIED Requirements

### Requirement: Append-only message log
`messages.jsonl` SHALL be an append-only file of newline-delimited JSON objects, each a dmon-owned record discriminated by a `type` field. A conversational turn is a `message` record `{type:"message", entryId, timestamp, role, parts}` where `parts` is an ordered list of dmon-owned `Part` records, each discriminated by `type`: `text`, `toolCall {callId, name, args}`, `toolResult {callId, result?, attachmentRef?, isError, truncated?}`, `image`, `reasoning`, `usage`, and `unknown {raw, producedBy}`. `args` and `result` are arbitrary JSON values (`JsonElement`). No third-party type (e.g. `Microsoft.Extensions.AI.ChatMessage`/`AIContent`) SHALL appear in the record schema. Records SHALL NOT be modified or deleted (except by compaction, which appends a marker — see below).

#### Scenario: Message appended after each turn
- **WHEN** a turn completes
- **THEN** a `message` record carrying the turn's `role` and `parts` (text, tool calls, and tool results as typed `Part`s) is appended to `messages.jsonl`

#### Scenario: Unmodelled content is preserved opaquely
- **WHEN** a turn contains content dmon does not model as a typed `Part`
- **THEN** it is preserved as an `unknown` part carrying the opaque `raw` payload and a `producedBy` stamp, and no third-party type name appears in the record

### Requirement: Attachment threshold
Tool results larger than a configurable byte threshold SHALL be offloaded **at persistence time** by session-storage: the full result is written to `attachments/<callId>.<ext>` and the `toolResult` part records `{preview, attachmentRef}` instead of the inline `result`. Smaller results are inlined in the `toolResult` part. The threshold is read from `IConfiguration` at `Daemon:Session:AttachmentThresholdBytes`, defaulting to `1024`. Offloading happens once, at write time; there is no separate provider-input offloading pass.

#### Scenario: Small output inlined
- **WHEN** a tool produces output smaller than the threshold
- **THEN** the `toolResult` part stores the output inline in its `result`

#### Scenario: Large output stored as attachment
- **WHEN** a tool produces output larger than the threshold
- **THEN** session-storage writes the full output to `attachments/<callId>.txt` and the `toolResult` part records `attachmentRef` plus a truncated `preview` (full output remains recoverable from the attachment)

#### Scenario: Threshold overridden via configuration
- **WHEN** `Daemon:Session:AttachmentThresholdBytes` is set to a custom value
- **THEN** that value is used as the threshold for all subsequent tool output decisions

### Requirement: Non-destructive compaction
Compaction SHALL append a `CompactionMessage` (`{type:"compaction", …}`) to `messages.jsonl` rather than deleting or rewriting prior records. All original records are preserved. The `compaction` line and the `message` line are the two members of the log union, both dmon-owned and discriminated by `type`.

#### Scenario: Compaction marker appended
- **WHEN** compaction is triggered
- **THEN** a `CompactionMessage` with `supersedesUpTo`, `summary`, `reason`, and `tokensBefore` is appended to `messages.jsonl`

#### Scenario: Reader respects compaction marker
- **WHEN** `messages.jsonl` is read and contains a `CompactionMessage`
- **THEN** all records with `entryId` ≤ `supersedesUpTo` are skipped; the summary is used as the effective context start

#### Scenario: Multiple compactions supported
- **WHEN** `messages.jsonl` contains multiple `CompactionMessage` records
- **THEN** the last one takes precedence

### Requirement: Session fork and clone
The system SHALL support forking a session at a specific `entryId` and cloning an entire session.

#### Scenario: Fork creates new session from entry point
- **WHEN** the host sends `session.fork {entryId}`
- **THEN** a new session directory is created by copying the source directory, the *new* session's `messages.jsonl` is truncated after the line containing `entryId` (the source file is never mutated), `attachments/` referenced by retained `toolResult` parts are preserved in the copy, and `meta.json` is rewritten with a new id, `parentSession` = source id, and `forkEntryId` = `entryId`

#### Scenario: Clone duplicates entire session
- **WHEN** the host sends `session.clone`
- **THEN** a new session is created as an exact copy with a new id and `parentSession` set to the source session id
