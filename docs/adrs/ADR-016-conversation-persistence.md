# ADR-016: Conversation Persistence — dmon-Owned Lossless Turn Record, Session-Storage-Owned, Memory-Derived

**Date:** 2026-06-01
**Status:** Accepted
**Amends:** ADR-004 (the `messages.jsonl` record shape and the `UserMessage`/`AssistantMessage`/`ToolResultMessage` naming); ADR-014 (the characterisation of `messages.jsonl` as text-only conversational turns written by the memory tier)
**References:** ADR-001 (M.E.AI is an internal abstraction, not a contract type), ADR-015 (typed, describable wire contract — `session.getMessages` DTO is deferred to this change)

## Context

The conversation is currently represented in three places, none lossless and none authoritative for resume:

1. **`TurnHandler._history`** (`IList<ChatMessage>`) — the actual LLM context. Assistant turns are stored as `new ChatMessage(ChatRole.Assistant, fullText)` — **text only**, tool calls dropped — and the list is **never rehydrated from disk**: a fresh core resumes a session with cold context.
2. **`messages.jsonl`** — written by the **memory tier** (`ShortTermMemory.RecordAsync`) as a byproduct of embedding/indexing. Each line is a `TurnLineRecord {entryId, timestamp, role, text, scope}` — text-only, because `text` is the embedding input. The `UserMessage`/`AssistantMessage`/`ToolResultMessage` records named by ADR-004 / the `session-storage` spec **do not exist in code**.
3. **The live event stream** (`messageDelta`, `toolExecutionStart/End`, …) — the only rich representation, and per ADR-014 it is never persisted.

Two related facts compound this:

- **Ownership is inverted.** `MessageAppender` lives in session-storage (`Dmon.Core/Session`) but is *called by* the memory tier — memory reaches down into storage to write the canonical file. `IMemoryStore.RecordAsync`'s contract even promises "the canonical JSONL is durable" on return.
- **Attachment offloading is in the wrong place for persistence.** `AttachmentOffloadingChatClient` offloads large `FunctionResultContent` — but on the messages **sent to the provider** (context-window management), not on what is persisted. The `session-storage` spec ties offloading to `ToolResultMessage` *persistence*.
- **`entryId` is minted by the memory tier** (`ShortTermMemory` Guid), yet `fork`'s `forkEntryId` and compaction's `supersedesUpTo` (ADR-004) reference the same id space.

Two forks were decided during exploration:

- **Fork 1 — ownership:** session-storage owns the lossless canonical record; the memory tier derives its index/distillation from turns it is handed.
- **Fork 2 — representation:** dmon owns the record's type; we do not put third-party types in the contract.

Fork 2 was sharpened into a standing principle (below).

## Decision

### 1. Session-storage owns the canonical conversation log; memory derives

The canonical, lossless conversation record is written by **session-storage**, not the memory tier. At turn completion the orchestration point (the turn handler) (a) appends the lossless record to the session log via session-storage, then (b) hands the turns to `IMemory.RecordAsync` for **indexing and distillation only**.

`IMemory` / `IShortTermMemory` / `ILongTermMemory` own **no** canonical persistence after this change — both tiers are derivations over the canonical log. `IMemoryStore.RecordAsync`'s contract is amended: it no longer guarantees canonical durability; it ingests turns into a derived, rebuildable index. `ShortTermMemory.RebuildFromJsonlAsync` becomes a first-class, lossless recovery path (delete `index.db`, rebuild from the rich log), not a degraded one.

### 2. Principle — no third-party types in the API definition

> dmon's **API definition surface** — the RPC event/command schema, the persisted-record schema, and therefore any generated non-.NET client contract — contains **dmon-owned types only**. Third-party types (`Microsoft.Extensions.AI.ChatMessage` / `AIContent` and subtypes) are **internal** and MUST NOT appear in the contract.

This generalises ADR-015. `ChatMessage` remains the in-loop working type (ADR-001); it is mapped to/from the dmon record at the storage boundary. It is the precise form of the earlier phrasing "never serialise third-party types", which wrongly forbade opaque preservation (see §4).

### 3. The record is a dmon-owned parts model

The session log is a union of line types, discriminated by `type`:

```
  message    { type:"message", entryId, timestamp, role, parts: Part[] }
  compaction { type:"compaction", … }      // existing CompactionMessage; already dmon-owned
```

`Part` is a dmon-owned polymorphic union discriminated by `type`:

```
  TextPart       { type:"text",       text }
  ToolCallPart   { type:"toolCall",   callId, name, args:   JsonElement }
  ToolResultPart { type:"toolResult", callId, result?: JsonElement, attachmentRef?, isError, truncated? }
  ImagePart      { type:"image",      mediaType, attachmentRef | dataBase64 }   // when multimodal lands
  ReasoningPart  { type:"reasoning",  text }                                    // record-only (see §5)
  UsagePart      { type:"usage",      … }                                       // record-only
  UnknownPart    { type:"unknown",    raw: JsonElement, producedBy }            // see §4
```

Rationale for **parts over** ADR-004's role-typed `UserMessage`/`AssistantMessage`/`ToolResultMessage` records: a single assistant turn legitimately contains text **and** multiple tool calls in one message — exactly `ChatMessage.Contents`. Parts map 1:1 to `AIContent`, keeping the bidirectional mapping mechanical, and the `Part` union is the same `[JsonPolymorphic]` machinery ADR-015 uses — so this record **is** the `session.getMessages` DTO (`SessionMessagesResultEvent`), describable by `JsonSchemaExporter`. The `session-storage` spec is reconciled: role-typed message names become `role` values + parts.

`args` and `result` are `JsonElement` — **intrinsic** opacity (tool schemas are arbitrary), not a missing contract. The part envelope is fully typed; only the user-defined tool payload is free-form, as it must be. `JsonElement` is BCL, not a named third-party type, so the principle holds.

### 4. Lenient mapping via an opaque, render-only `UnknownPart`

The `ChatMessage → record` mapping is **lenient**: an `AIContent` subtype dmon does not model is preserved in a dmon-owned opaque envelope `UnknownPart { raw: JsonElement, producedBy }` rather than dropped or thrown on. The third-party shape lives *inside* the opaque blob; it is never *named* in the schema — so the principle in §2 holds and clients render `type:"unknown"` gracefully.

`UnknownPart` is **record-only / non-replayable**: the `record → ChatMessage` rehydration **skips** it. Consequently dmon only ever *writes* the opaque blob and never deserialises `raw` back into an `AIContent`, so there is **no version-coupled read path** — an M.E.AI upgrade cannot break replay of old unknown blobs. `producedBy` stamps the producing library version so a future migration *could* re-interpret old blobs if it ever chose to.

### 5. Lossless record, subset replay

The record is a **superset** persisted for the canonical log, audit, and client rendering. The `record → ChatMessage` **replay subset** fed back into the loop is `{ TextPart, ToolCallPart, ToolResultPart, ImagePart }`. `ReasoningPart`, `UsagePart`, and `UnknownPart` are persisted but **not** replayed into LLM context. One record, two consumers (the loop vs. the client/audit).

### 6. Attachment offloading happens once, at write-time

Offloading a large tool result is performed **once, at persistence time** by session-storage, per ADR-004's threshold: the full result is written to `attachments/<callId>` and the record's `ToolResultPart` carries `{ preview, attachmentRef }` (record + attachment together remain lossless). On resume, the `record → ChatMessage` replay reconstructs historical tool results in their already-shrunk preview form, so the provider naturally receives the small version without a second offloading pass.

The existing provider-input middleware `AttachmentOffloadingChatClient` is therefore **removed** — its context-window job is subsumed by write-time offloading plus preview-form replay. There is a single offloading mechanism and a single threshold; the canonical attachment is the persisted one. (Within-turn handling of the *current* turn's tool result is the live agent loop's concern and is unaffected.)

### 7. `entryId` is minted by session-storage and passed through

Session-storage mints `entryId` when it appends a record and **passes it through** to `IMemory.RecordAsync`, so the index, `fork`'s `forkEntryId`, and compaction's `supersedesUpTo` all reference the same id as the canonical log. `ShortTermMemory` no longer mints ids.

## Consequences

- **Session resume becomes faithful.** Reloading a session rehydrates `_history` from the canonical record (replay subset), so a fresh core resumes with full tool-call/result context — fixing a current correctness gap, not just a remote-client nicety.
- **ADR-015's `session.getMessages` is unblocked.** The parts record is the typed DTO; `session.getMessages` converts from its quarantined legacy path to `SessionMessagesResultEvent` once this lands.
- **The memory index is fully derived and disposable.** Recovery is "delete `index.db`, rebuild from the rich log" with no loss.
- **Breaking on-disk format, no migration.** There are no production deployments or real session data yet, so no backward-compatibility or migration path is required: `TurnLineRecord` is removed outright and any existing local session directories may be discarded. The change does **not** carry legacy-read code.
- **Recurring mapping maintenance.** `ChatMessage ⇆ record` mapping must track M.E.AI's `AIContent` types. Lenient `UnknownPart` bounds the blast radius of an unmodelled type to render-only preservation rather than data loss or a crash.
- **Spec reconciliation.** `session-storage` (ADR-004) is updated to the parts model; the offloading requirement is re-homed to write-time; `IMemoryStore.RecordAsync`'s durability clause is corrected.
- **Implementation is a separate OpenSpec change.** This ADR records the decisions; it does not implement them. The change depends on / coordinates with the accepted ADR-015 work (it supplies the deferred `getMessages` DTO).

## Resolved Questions

- **Provider-input offloading reconciliation.** Resolved (§6): a single write-time offloading mechanism; `AttachmentOffloadingChatClient` is removed and replaced by preview-form replay. Persisting full result vs. sending a shrunk form is not independently tunable, which is accepted (YAGNI).
- **Reasoning replay.** Resolved: reasoning is **record/UI-only, never replayed on resume**. This is safe because resume occurs only at **turn boundaries** (ADR-014 — an interrupted in-flight turn is lost on restart), so within-turn reasoning continuity is never required across a resume; the signed-thinking-block echo that some providers (e.g. Anthropic extended thinking + tools) need within a turn is handled live, in-memory, by the provider pipeline before persistence and is unaffected. Revisit only if a provider requires *cross-turn* reasoning continuity.
