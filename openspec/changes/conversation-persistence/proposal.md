## Why

The conversation is represented in three places, none lossless and none authoritative for resume: `TurnHandler._history` (text-only, never rehydrated from disk), `messages.jsonl` (text-only `TurnLineRecord`, written by the *memory tier* as a byproduct of embedding), and the live event stream (rich, never persisted). Consequently **resuming a session in a fresh core starts the model with cold context** — a correctness bug, not just a remote-client gap — and ADR-015's `session.getMessages` DTO cannot be typed. ADR-016 resolves this: session-storage owns a lossless, dmon-owned record; the memory tier derives its index from it.

## What Changes

- **BREAKING (on-disk):** Replace `TurnLineRecord` with a lossless, dmon-owned **parts** record. Log lines are a union (`message` / `compaction`); a `message` is `{entryId, timestamp, role, parts[]}`; `Part` is a polymorphic union (`text`, `toolCall`, `toolResult`, `image`, `reasoning`, `usage`, `unknown`). No migration — no production data exists.
- **Invert persistence ownership:** session-storage writes the canonical log; the memory tier (`IMemory.RecordAsync`) is handed turns for **indexing/distillation only** and owns no canonical persistence. `IMemoryStore.RecordAsync`'s durability contract is corrected accordingly.
- **Faithful session resume:** on `session.load`, `TurnHandler._history` is rehydrated from the canonical record (replay subset → `ChatMessage`), restoring tool-call/result context.
- **No third-party types in the API definition** (the standing principle from ADR-016): `ChatMessage`/`AIContent` stay internal; the persisted-record and wire schemas are dmon-owned. Unmodelled `AIContent` is preserved as render-only opaque `UnknownPart` (lenient mapping); `JsonElement` carries intrinsically free-form tool `args`/`result`.
- **Attachment offloading moves to write-time and is unified:** large tool results are offloaded once, at persistence, into `attachments/<callId>` with `ToolResultPart {preview, attachmentRef}`; the provider-input middleware `AttachmentOffloadingChatClient` is **removed** (subsumed by preview-form replay).
- **`entryId` is minted by session-storage** at append time and passed through to the memory tier, so the index, `forkEntryId`, and compaction `supersedesUpTo` share one id space.
- **Un-quarantine `session.getMessages`:** it converts from the legacy untyped path (left in place by the typed-command-result-events change) to a typed `SessionMessagesResultEvent` carrying the parts record — completing ADR-015's deferred item.

## Capabilities

### New Capabilities
<!-- None — this change reshapes existing capabilities; it introduces no new one. -->

### Modified Capabilities
- `session-storage`: the canonical `messages.jsonl` record becomes the dmon-owned parts model; session-storage owns the write and mints `entryId`; attachment offloading is performed at write-time; `TurnLineRecord` is removed.
- `memory`: `IMemoryStore.RecordAsync` derives a (rebuildable) index from turns it is handed and owns no canonical persistence; it consumes the `entryId` minted by session-storage; rebuild-from-log becomes a lossless recovery path.
- `attachment-offloading`: offloading occurs once at persistence write-time (`ToolResultPart.attachmentRef`); the provider-input `AttachmentOffloadingChatClient` middleware is removed; preview-form replay supplies the shrunk version to the provider.
- `agent-core`: `session.load` rehydrates conversation history into the turn loop (replay subset); `session.getMessages` returns a typed `SessionMessagesResultEvent` carrying the parts record.

## Impact

- **Code:** new dmon-owned record/part types and `ChatMessage ⇆ record` mapping (lenient, `UnknownPart` for unmodelled content; replay subset = `text`/`toolCall`/`toolResult`/`image`); session-storage gains the canonical append + typed read-back + `entryId` minting + write-time offloading; `ShortTermMemory.RecordAsync` stops writing `messages.jsonl` and stops minting ids; `TurnHandler` rehydrates `_history` on load; `AttachmentOffloadingChatClient` deleted; `SessionMessagesResultEvent` added (un-quarantining `session.getMessages`).
- **Specs:** `session-storage`, `memory`, `attachment-offloading`, `agent-core` (deltas in this change); reconciles ADR-004's aspirational `UserMessage`/`AssistantMessage`/`ToolResultMessage` naming to the parts model.
- **ADRs:** implements ADR-016; completes ADR-015's deferred `session.getMessages`; references ADR-001 (M.E.AI internal), ADR-014 (resume at turn boundaries → reasoning never replayed).
- **Dependency / ordering:** depends on the `typed-command-result-events` change (which establishes `ResultEvent` and leaves the `session.getMessages` quarantine this change removes). Apply typed-command-result-events first.
- **No migration:** no production deployments or real session data exist; `TurnLineRecord` and any local session dirs are discarded, not migrated.
