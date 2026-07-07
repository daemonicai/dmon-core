## Why

`messages.jsonl` appends are never flushed to disk with a durability barrier (`MessageAppender.WriteLineAsync` writes and lets the stream close without `fsync`), so a process crash mid-append can leave a **partial trailing line**. That is an *expected* post-crash state, and `SessionStore.ReadRecordsAsync` already tolerates it by skipping any line that fails to deserialize. But the sibling reader `SessionStore.ReadMessagesAsync` — the memory-tier read path (`IShortTermMemory.ReadMessagesAsync`) — deserializes each line with **no error handling** (three call sites), so a single bad line makes the whole read throw `JsonException`. The two readers over the same file disagree on the crash model, and the fragile one can lock a session out of its own history.

## What Changes

- Make `SessionStore.ReadMessagesAsync` tolerant of malformed/partial JSON lines: a line that fails to deserialize is skipped rather than aborting the entire read, matching the already-tolerant `ReadRecordsAsync`.
- Preserve compaction semantics: line-index arithmetic (`lastCompactionIndex`, `supersedesUpToIndex`) must stay consistent when a malformed line is skipped at materialization time.
- Add regression tests: a `messages.jsonl` with a partial trailing line (and a malformed interior line) reads back the well-formed records via `ReadMessagesAsync`, with and without `applyCompaction`.

No wire-protocol, API-surface, or persistence-format change. This is a read-path robustness fix confined to `core/Dmon.Core`.

## Capabilities

### New Capabilities
<!-- none -->

### Modified Capabilities
- `session-storage`: the append-only-message-log reader requirement gains a durability/tolerance scenario — reads SHALL skip a malformed or partial trailing line (the expected post-crash state given non-fsync'd appends) rather than failing the whole read.

## Impact

- **Code:** `core/Dmon.Core/Session/SessionStore.cs` — `ReadMessagesAsync` (deserialize call sites at ~307, ~339, ~379).
- **Consumers:** `IShortTermMemory.ReadMessagesAsync` callers (memory tier); no signature change.
- **Tests:** `test/Dmon.Core.Tests/Session/` (new tolerance cases alongside the existing `CompactionTests`/`MessagePersistenceTests`).
- **Spec:** `openspec/specs/session-storage/spec.md` — delta on the append-only message-log requirement.
