## Purpose

Define the session-as-relocatable-directory storage model: the on-disk layout (`messages.jsonl`, `meta.json`, `attachments/`), session-storage ownership of the canonical conversation record (the dmon-owned parts model), append-only message logging, write-time attachment offloading, non-destructive compaction, project-local-vs-global store discovery, session fork and clone operations, and the SQLite session index.

## Requirements

### Requirement: Session-as-relocatable-directory
Each session SHALL be stored as a self-contained directory that can be copied, moved, or shared without losing any content. No data outside the session directory is required to read session content.

#### Scenario: Session directory is self-contained
- **WHEN** a session directory is copied to a new location
- **THEN** all messages, metadata, and attachments are accessible from the new location without reconfiguration

### Requirement: Session directory layout
Each session directory SHALL contain `messages.jsonl`, `meta.json`, and an `attachments/` subdirectory.

#### Scenario: New session creates correct layout
- **WHEN** a new session is created
- **THEN** the session directory contains `messages.jsonl` (empty), `meta.json` (with id, name, created timestamp), and an `attachments/` subdirectory

### Requirement: Session-storage owns the canonical conversation record
Session-storage SHALL own the write of the canonical conversation log. At turn completion the orchestration point SHALL append the turn(s) to `messages.jsonl` via session-storage **before** the turns are handed to the memory tier for indexing. The memory tier SHALL NOT write `messages.jsonl`. Session-storage SHALL mint each record's `entryId` at append time and SHALL supply that `entryId` to the memory tier, so the derived index, `forkEntryId`, and compaction `supersedesUpTo` reference one id space.

#### Scenario: Canonical append precedes indexing
- **WHEN** a turn completes
- **THEN** session-storage appends the turn's record to `messages.jsonl` and mints its `entryId`, and only then are the turns passed to the memory tier for index/distillation

#### Scenario: entryId is owned by session-storage
- **WHEN** a turn is recorded
- **THEN** the `entryId` written to `messages.jsonl` is the same id the memory index keys on (the memory tier does not mint its own)

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

### Requirement: Session discovery — project-local by default
The system SHALL discover the session store by walking up the directory tree from CWD, looking for a `.daemon/` directory. If found, sessions are stored in `.daemon/sessions/`. If not found, the fallback is `~/.daemon/sessions/`.

#### Scenario: Project-local store used when .daemon/ exists
- **WHEN** the agent is invoked from a directory with a `.daemon/` directory in its ancestor tree
- **THEN** sessions are stored in that `.daemon/sessions/` directory

#### Scenario: Global store used when no .daemon/ found
- **WHEN** no `.daemon/` directory exists in the ancestor tree and the user has not opted into project-local storage
- **THEN** sessions are stored in `~/.daemon/sessions/`

#### Scenario: First-use bootstrap creates .daemon/ at CWD
- **WHEN** a user starts their first session in a project that has no `.daemon/` in its ancestor tree and config opts into project-local storage (the default)
- **THEN** the core creates `.daemon/` at CWD with a default `config.yaml` and empty `sessions/`, then emits `bootstrapNotice {path, created[]}` listing the files written before continuing

#### Scenario: Store redirected to global via config
- **WHEN** `.daemon/config.yaml` contains `sessionStore: global`
- **THEN** sessions are stored in `~/.daemon/sessions/` regardless of project-local directory presence

### Requirement: Session fork and clone
The system SHALL support forking a session at a specific `entryId` and cloning an entire session.

#### Scenario: Fork creates new session from entry point
- **WHEN** the host sends `session.fork {entryId}`
- **THEN** a new session directory is created by copying the source directory, the *new* session's `messages.jsonl` is truncated after the line containing `entryId` (the source file is never mutated), `attachments/` referenced by retained `toolResult` parts are preserved in the copy, and `meta.json` is rewritten with a new id, `parentSession` = source id, and `forkEntryId` = `entryId`

#### Scenario: Clone duplicates entire session
- **WHEN** the host sends `session.clone`
- **THEN** a new session is created as an exact copy with a new id and `parentSession` set to the source session id

### Requirement: Global session index
A SQLite database at `<store-root>/sessions.db` SHALL maintain an index of sessions for fast listing. The index is a cache and SHALL be rebuildable by scanning session directories.

#### Scenario: Session list returns index contents
- **WHEN** the host sends `session.list`
- **THEN** the response is populated from `sessions.db` without reading individual `meta.json` files

#### Scenario: Index rebuilt after corruption
- **WHEN** `sessions.db` is missing or corrupt
- **THEN** the system scans all session directories, reads each `meta.json`, and rebuilds the index
