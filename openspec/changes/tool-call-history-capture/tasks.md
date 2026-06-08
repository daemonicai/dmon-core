## 1. AttachmentStore callId path-traversal guard

- [x] 1.1 In `AttachmentStore`, before constructing the attachment filename from `callId`, validate it: reject empty, path separators (`/`, `\`), `..`, or any value that could escape `attachments/`. For an unsafe `callId`, derive a deterministic, collision-resistant safe filename (e.g. a stable hash/escape) so distinct ids map to distinct files and the returned `attachmentRef` still resolves.
- [x] 1.2 Ensure the resolved write path is provably inside the session's `attachments/` directory (e.g. full-path containment check) and never resolves outside it.
- [x] 1.3 Unit tests: safe `callId` written as `attachments/<callId>.<ext>`; `callId = "../../etc/evil"` stays inside `attachments/` under a derived name and writes no file outside it; two distinct unsafe ids do not collide. Build and tests green.

## 2. Capture structured tool content into history

- [x] 2.1 In `TurnHandler.RunTurnAsync`, extend the streaming loop to collect each `FunctionCallContent` and `FunctionResultContent` (correlated by `callId`) in addition to text, without switching off streaming.
- [x] 2.2 Replace the text-only `_history.Add(new ChatMessage(ChatRole.Assistant, fullText))` with appending the turn in M.E.AI-canonical shape: an assistant `ChatMessage` carrying the accumulated `TextContent` plus the turn's `FunctionCallContent`(s), followed by a tool `ChatMessage` (`ChatRole.Tool`) carrying the `FunctionResultContent`(s) — the shape `ConversationMapper`/replay already round-trips.
- [x] 2.3 Confirm the existing per-turn persistence (`PersistNewHistoryEntriesAsync` → `ConversationMapper.ToParts` → write-time offloading → D6 reconciliation) records the tool parts with no new mapping code; do not add a parallel recording path.
- [x] 2.4 Build and tests green (existing turn/persistence tests still pass with structured history).

## 3. Real-result tool events

- [x] 3.1 Emit `toolExecutionEnd` when the matching `FunctionResultContent` (same `callId`) is observed, carrying the real result value and `isError`; remove the placeholder `{callId, name}` object and the `TODO(Group 9.5)` from `HandleToolCallAsync`.
- [x] 3.2 Accumulate the real results into `TurnEndEvent.ToolResults`.
- [x] 3.3 Handle the anomaly of a tool call with no observed result in the stream (provider/abort): emit `toolExecutionEnd` with an error marker rather than fabricating success or hanging; record whatever parts exist.
- [x] 3.4 Build and tests green.

## 4. Tests and finalisation

- [ ] 4.1 Integration test: a turn that calls a tool records assistant `toolCall` + tool `toolResult` parts in `messages.jsonl`; resuming in a fresh core restores them into context (replay subset).
- [ ] 4.2 Test: `toolExecutionEnd` carries the real tool result and correct `isError` (no placeholder); `TurnEndEvent.ToolResults` reflects real results.
- [ ] 4.3 Test: a large tool result is offloaded at write-time to `attachments/<safe-callId>` and the historical turn replays in preview form (D6) on the next turn.
- [ ] 4.4 Grep: no residual placeholder result / `TODO(Group 9.5)` in `TurnHandler`; no `Microsoft.Extensions.AI` type leaks into any persisted-record or wire payload.
- [ ] 4.5 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 4.6 `make test` (or `dotnet test -c Release`) green across all projects.
- [ ] 4.7 `openspec validate tool-call-history-capture --strict` passes.
</content>
