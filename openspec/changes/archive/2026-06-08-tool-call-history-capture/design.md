## Context

`conversation-persistence` built a complete, dmon-owned parts pipeline — `ToolCallPart`/`ToolResultPart`, `ConversationMapper` (`ChatMessage ⇆ record`), write-time attachment offloading, D6 history reconciliation, and faithful resume — but nothing feeds it tool content. Today `TurnHandler.RunTurnAsync` (`src/Dmon.Core/Rpc/TurnHandler.cs`) streams the response from the pipeline (`PermissionGateChatClient → FunctionInvokingChatClient → RetryingChatClient → provider`), with `ChatOptions.Tools` populated. M.E.AI's `FunctionInvokingChatClient` therefore **executes** the tools. But the streaming loop only does two things with the stream: it accumulates `TextContent` into a `StringBuilder`, and on `FunctionCallContent` it calls `HandleToolCallAsync`, which emits a `toolExecutionStart`/`toolExecutionEnd` pair with a **placeholder** result object (`new { callId, name }`, the `TODO(Group 9.5)`). At end of turn only `new ChatMessage(ChatRole.Assistant, fullText)` is appended to `_history` — pure text. The `FunctionResultContent` that `FunctionInvokingChatClient` produces is streamed past and dropped.

The consequences: tool calls/results are never persisted (the parts pipeline never sees them), never replayed on resume, and the `toolExecutionEnd` event carries no real result. This change captures the structured content the loop already receives.

Constraints: M.E.AI types stay internal (ADR-001) — captured `FunctionCallContent`/`FunctionResultContent` are mapped to dmon-owned parts by the existing `ConversationMapper` before persistence. Tool execution stays behind the permission gate (ADR-006); this change does not move or re-order the pipeline.

## Goals / Non-Goals

**Goals:**
- After a turn with tool use, `_history` (and therefore the persisted log) contains the assistant's tool calls and the tool results as structured content, in the M.E.AI-canonical shape the existing `ConversationMapper`/replay already expects — so persistence and resume round-trip them with no new mapping code.
- `toolExecutionEnd` and `TurnEndEvent.ToolResults` carry the real result value and `isError`, not a placeholder.
- A live tool-call `callId` can never cause an attachment write outside the session's `attachments/` directory.

**Non-Goals:**
- Changing how tools execute, the pipeline composition/order, or the permission gate.
- Switching the turn loop away from streaming to a non-streaming response model.
- Multimodal tool results / images, reasoning capture (already part-typed by `conversation-persistence`; not produced here).
- Any change to the wire shape of `toolExecutionStart`/`toolExecutionEnd`/`turnEnd` beyond the `result` payload now being real.

## Decisions

### D1 — Capture structured content from the existing streaming loop
The same `await foreach` over `pipeline.GetStreamingResponseAsync` collects, in addition to text: every `FunctionCallContent` and every `FunctionResultContent` surfaced by `FunctionInvokingChatClient`. No switch to `GetResponseAsync`. Rationale: the streaming loop already drives all live `messageDelta`/`toolExecution*` events; keeping one pass preserves the streaming UX and avoids a second provider round-trip. Alternative considered — call the non-streaming `GetResponseAsync` and read `ChatResponse.Messages` (which M.E.AI populates with the full call/result sequence): cleaner data but loses incremental streaming to the host; rejected.

### D2 — Append history in M.E.AI-canonical shape, replacing the text-only append
The current `_history.Add(new ChatMessage(ChatRole.Assistant, fullText))` is replaced by appending the turn's messages in the shape `ConversationMapper.ToMessage`/replay already round-trips: an **assistant** `ChatMessage` whose `Contents` carry the accumulated `TextContent` plus the turn's `FunctionCallContent`(s), followed by a **tool** `ChatMessage` (`ChatRole.Tool`) carrying the `FunctionResultContent`(s). Rationale: this is exactly the structure the parts mapper and the replay subset assume (`TextContent`→`TextPart`, `FunctionCallContent`→`ToolCallPart`, `FunctionResultContent`→`ToolResultPart`), so persistence, write-time offloading, D6 reconciliation, and resume all work unchanged. The existing per-turn persistence slice (`PersistNewHistoryEntriesAsync`) then offloads/records the tool result with no new code. Alternative — invent a bespoke history entry: rejected; it would force parallel mapping logic and break the single-vocabulary guarantee.

### D3 — Event fidelity: real result, correlated by callId
`toolExecutionStart` is emitted when a `FunctionCallContent` is observed (as today); `toolExecutionEnd` is emitted when the matching `FunctionResultContent` (same `callId`) is observed, carrying the real result and `isError`. `HandleToolCallAsync` stops fabricating a result. `TurnEndEvent.ToolResults` accumulates the real results. Edge case — a tool call with no observed result in the stream (provider/abort anomaly): emit `toolExecutionEnd` with an error marker rather than hang or fabricate success, and still record whatever parts exist. Rationale: the host's tool UI must reflect reality; correlation by `callId` is already the model's identity for a call.

### D4 — `callId` sanitisation owned by `AttachmentStore`
`AttachmentStore` is the sole constructor of the attachment filename (`<callId>.<ext>`), so the guard lives there. Before building the path, validate `callId`: if it is empty, contains path separators (`/`, `\`), `..`, or other characters that could escape `attachments/`, derive a deterministic safe filename instead (sanitised/encoded) so the write always stays inside the session's `attachments/` directory and the returned `attachmentRef` still resolves. Rationale: never lose data and never write out of bounds; a malformed/hostile `callId` degrades to a safe filename rather than crashing the (best-effort) persistence path. Alternative — throw on unsafe `callId`: rejected because persistence is best-effort and a provider-supplied id should not be able to abort a turn; a hard reject would also lose the tool result. The guard is validated by a unit test feeding `callId = "../../etc/evil"` and asserting the write stays within `attachments/`.

## Risks / Trade-offs

- **M.E.AI streaming may split a call/result across multiple updates or order them unexpectedly** → accumulate by `callId` across the stream and assemble the canonical messages only at end-of-turn; do not assume one-update-per-call.
- **Double-counting if both the manual loop and a future path record results** → there is one capture site (the streaming loop); `HandleToolCallAsync` is reduced to event emission, not a second recording path.
- **History growth / context bloat from full tool results in `_history`** → already handled by `conversation-persistence` D6: completed turns are reconciled to preview form after persistence, so subsequent turns send shrunk historical results. This change relies on that mechanism, not a new one.
- **Unsafe `callId` encoding collisions** (two unsafe ids mapping to one filename) → use a deterministic encoding that preserves uniqueness (e.g. a hash or reversible escape), not a lossy replace.

## Migration Plan

No data migration (no production data). Implementation order: (1) `AttachmentStore` `callId` sanitisation + test (independent, lands first so the offload path is safe before live ids flow); (2) capture `FunctionCallContent`/`FunctionResultContent` in the streaming loop and append history in canonical shape (D1/D2); (3) real-result events (D3), removing the placeholder; (4) tests — round-trip a tool turn through persist→resume, assert tool parts present and replayed, assert real result in `toolExecutionEnd`; (5) spec deltas. Rollback = revert (the parts pipeline simply receives no tool content again).

## Open Questions

- None. Tool-result fidelity, offloading, and replay semantics were all resolved by `conversation-persistence`; this change only feeds them.
</content>
