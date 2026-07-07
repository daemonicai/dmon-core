## MODIFIED Requirements

### Requirement: Append-only message log
`messages.jsonl` SHALL be an append-only file of newline-delimited JSON objects, each a dmon-owned record discriminated by a `type` field. A conversational turn is a `message` record `{type:"message", entryId, timestamp, role, parts}` where `parts` is an ordered list of dmon-owned `Part` records, each discriminated by `type`: `text`, `toolCall {callId, name, args}`, `toolResult {callId, result?, attachmentRef?, isError, truncated?}`, `image`, `reasoning`, `usage`, and `unknown {raw, producedBy}`. `args` and `result` are arbitrary JSON values (`JsonElement`). No third-party type (e.g. `Microsoft.Extensions.AI.ChatMessage`/`AIContent`) SHALL appear in the record schema. Records SHALL NOT be modified or deleted (except by compaction, which appends a marker — see below).

Because appends are not flushed with a durability barrier, a process crash mid-append can leave a partial (truncated) trailing line; this is an expected post-crash on-disk state. Every reader of `messages.jsonl` SHALL tolerate a line that fails to deserialize as valid JSON — the malformed line SHALL be skipped and the remaining well-formed records SHALL be returned — so that no single bad line can fail the read of an otherwise valid session. This tolerance SHALL be consistent across all readers of the file.

#### Scenario: Message appended after each turn
- **WHEN** a turn completes
- **THEN** a `message` record carrying the turn's `role` and `parts` (text, tool calls, and tool results as typed `Part`s) is appended to `messages.jsonl`

#### Scenario: Unmodelled content is preserved opaquely
- **WHEN** a turn contains content dmon does not model as a typed `Part`
- **THEN** it is preserved as an `unknown` part carrying the opaque `raw` payload and a `producedBy` stamp, and no third-party type name appears in the record

#### Scenario: Partial trailing line tolerated after a crash
- **WHEN** `messages.jsonl` ends with a partial (truncated, non-parseable) trailing line left by a crash mid-append
- **THEN** the reader skips the partial line and returns all preceding well-formed records rather than failing the entire read

#### Scenario: Readers agree on the crash model
- **WHEN** the same `messages.jsonl` containing a malformed line is read by any reader (the typed-record reader or the raw-element reader)
- **THEN** each reader skips the malformed line and returns the well-formed records, so the readers do not disagree on whether the file is readable
