## Context

This change implements ADR-016 (Accepted). The binding decisions — session-storage owns the canonical log; the dmon-owned parts record; "no third-party types in the API definition"; lenient render-only `UnknownPart`; record-superset/replay-subset; write-time offloading; `entryId` minted by session-storage — are in `docs/adrs/ADR-016-conversation-persistence.md` and are not re-argued here. This document covers the implementation-shaping decisions and the wiring of the ownership inversion. It depends on the `typed-command-result-events` change (ADR-015) for the `ResultEvent` base and the `session.getMessages` quarantine this change removes.

## Goals / Non-Goals

**Goals:**
- A dmon-owned parts record that is the single canonical conversation representation, written by session-storage, and that doubles as ADR-015's `session.getMessages` DTO.
- Faithful session resume: a fresh core reloads tool-call/result context, not just text.
- The memory index is a pure, rebuildable derivation of the canonical log.
- One offloading mechanism (write-time), no third-party types in any contract surface.

**Non-Goals:**
- Multimodal/image capture and reasoning capture beyond defining the part types — they are part-typed for forward-compat but only `text`/`toolCall`/`toolResult` flow today (and `image` once multimodal input lands elsewhere).
- Any migration or legacy-read path (no production data — see proposal).
- Changing the live event stream, the turn lifecycle, or within-turn provider handling (incl. signed-thinking blocks).

## Decisions

### D1 — The mapping lives at the session-storage boundary, both directions

A single mapping component owns `ChatMessage → record` (write) and `record → ChatMessage` (replay). Write maps each `AIContent` to its Part (`TextContent`→`TextPart`, `FunctionCallContent`→`ToolCallPart`, `FunctionResultContent`→`ToolResultPart`); an unrecognised `AIContent` becomes `UnknownPart {raw, producedBy}` (lenient). Replay reconstructs only the **replay subset** (`text`/`toolCall`/`toolResult`/`image`) into `ChatMessage.Contents`, skipping `reasoning`/`usage`/`unknown`. Rationale: keeping both directions in one place keeps the lossy/lossless boundary explicit and testable, and guarantees the write side and the replay side agree on the part vocabulary.

### D2 — Turn-completion orchestration: storage first, then memory

The orchestration point (turn handler) appends the record via session-storage **first** (canonical, durable), then calls `IMemory.RecordAsync(turns)` for indexing. `entryId` is minted by the append and passed to memory so the index keys match the log. `ShortTermMemory.RecordAsync` no longer calls `IMessageAppender` and no longer mints ids; it consumes the supplied `entryId` and builds only the index. Rationale: enforces ADR-016's ownership inversion at the call site and makes "canonical durable before indexed" the literal code order.

### D3 — Replay subset reconstruction on `session.load`

`session.load` reads the canonical log via session-storage, maps it through D1's replay path, and seeds `TurnHandler._history`. Compaction markers are honoured (entries with `entryId ≤ supersedesUpTo` are skipped, the summary seeds context) — the same rule the memory tier's reader already applies. Rationale: resume fidelity is the headline correctness win; reusing the compaction-aware read keeps one interpretation of the log. Note (decided in group 5): the compaction summary is replayed as a synthetic `ChatRole.Assistant` message in file-position order; the memory tier never reconstructs a `ChatMessage` from it, so this interpretation is first established here.

### D4 — Write-time offloading replaces the provider-input pass

Session-storage performs the ADR-004 threshold check on each `FunctionResultContent` at append: over threshold → full result to `attachments/<callId>`, `ToolResultPart {preview, attachmentRef}`; under → inline `result`. `AttachmentOffloadingChatClient` is deleted; replay reconstructs offloaded results in preview form, so the provider receives the shrunk version without a second pass. Rationale: ADR-016 §6; one threshold, one owner; record+attachment stays lossless.

### D6 — `_history` for completed turns is sourced from the persisted (preview) form

Removing `AttachmentOffloadingChatClient` means the in-flight turn's tool-use loop now sees the **full** tool result it just requested (correct — the model asked for the data). But *subsequent* turns must not resend full historical results, or live sessions bloat the context window. The unifying rule: once a turn completes and is persisted (with write-time offloading), the turn handler reconciles that turn's entries in `_history` to the **replay/preview form** — the same `record → ChatMessage` path used on resume. Live continuation and cold resume therefore share one path (record → replay subset → `_history`), and preview-form context management applies uniformly without a pipeline middleware. The in-flight turn is the only place full results live in context, which is exactly where they should.

### D5 — Part union is the same polymorphism as ADR-015

`Part` and the log-line union use `[JsonPolymorphic(TypeDiscriminatorPropertyName="type")]` with `[JsonDerivedType]` leaves, so `JsonSchemaExporter` describes the record and `SessionMessagesResultEvent` (a `ResultEvent` from the typed-events change) carries it directly. `args`/`result` are `JsonElement`. Rationale: reuses ADR-015's machinery; the record *is* the wire DTO with no separate mapping.

## Risks / Trade-offs

- **`ShortTermMemory` rebuild path must read the new record** → `RebuildFromJsonlAsync` currently parses `TurnLineRecord`. Mitigation: it now parses the parts record and derives its index text from `TextPart`s; this is in-scope and tested. Rebuild becomes lossless rather than degraded.
- **Lossy replay could surprise** (reasoning/usage/unknown dropped from context) → Mitigation: this is ADR-016 §5 by design and safe per ADR-014 (resume at turn boundaries); documented in the spec scenario.
- **Mapping drift as M.E.AI evolves** → Mitigation: lenient `UnknownPart` bounds an unmodelled type to render-only preservation (no crash, no loss); `producedBy` stamps it for any future re-interpretation.
- **Ordering dependency on typed-command-result-events** → Mitigation: proposal records it; this change un-quarantines `session.getMessages` and assumes `ResultEvent` exists. If applied out of order, `session.getMessages` typing tasks block.

## Migration Plan

No data migration (no production data). Implementation order: (1) record + part types and the polymorphic tables; (2) `ChatMessage ⇆ record` mapping (write + replay subset + `UnknownPart`); (3) session-storage canonical append + typed read-back + `entryId` minting + write-time offloading; (4) invert turn-completion wiring and strip persistence/id-minting from `ShortTermMemory`; (5) rehydrate `_history` on `session.load`; (6) delete `AttachmentOffloadingChatClient`; (7) un-quarantine `session.getMessages` → `SessionMessagesResultEvent`; (8) update specs/tests. Rollback = revert.

## Open Questions

- None. ADR-016's two open questions are resolved in that ADR (single write-time offloading; reasoning record-only/no-replay).
