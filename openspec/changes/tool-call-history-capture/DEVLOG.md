# DEVLOG: tool-call-history-capture

<!-- Capture structured tool calls/results into history (persist + replay) and emit real tool-result events; guard AttachmentStore callId against path traversal. -->

## 1. AttachmentStore callId path-traversal guard

- `AttachmentStore.StoreIfLargeAsync` now validates `callId` before building the filename. Safe ids (allowlist `[A-Za-z0-9._-]`, non-empty, no `..` segment) are used verbatim as `attachments/<callId>.<ext>`; unsafe ids derive a deterministic `unsafe_<sha256-hex>` filename.
- **Decision:** SHA-256 hex of the raw callId for unsafe ids — collision-resistant (rejects the lossy `Replace` the design's Risks section calls out) and always filename-safe. Alternative reversible-escape rejected as more code for no benefit.
- **Decision:** never throw on an unsafe id (design D4 — persistence is best-effort; a hostile provider id must not abort a turn or lose the result). The `InvalidOperationException` after the full-path containment check is defensive-only and provably unreachable (derived names are separator-free; the allowlist excludes separators).
- Belt-and-suspenders: explicit `..` rejection is load-bearing because both dots are in the allowlist; the post-`Path.Combine` `GetFullPath` + trailing-separator boundary check is the real backstop and catches the sibling-prefix (`attachments-evil/`) false positive.
- Tests in `test/Dmon.Core.Tests/Session/AttachmentStoreTests.cs` cover the three spec scenarios, incl. a negative assertion that no file exists at the `../../etc/evil.txt` traversal target.
- **Note:** repo HAS a formal test project (`test/Dmon.Core.Tests/`) — the older "scaffold under sandbox/" guidance is stale.

## 2. Capture structured tool content into history

- `RunTurnAsync` streaming loop now accumulates, per `while(true)` iteration: `accumulatedCalls` (callId→`FunctionCallContent`, last-write-wins), `callOrder` (first-seen order), `accumulatedResults` (callId→`FunctionResultContent`), `startedCallIds` (HashSet). Still `GetStreamingResponseAsync` — no switch to non-streaming.
- **Decision:** accumulators declared INSIDE the `while(true)` loop body so each follow-up iteration (`continue`) gets fresh state — prevents a prior iteration's calls leaking into a later follow-up's assistant message.
- End-of-turn append (replaces text-only add): assistant `ChatMessage` = `[TextContent(fullText), ...calls in callOrder]`, then (only if results non-empty) a tool-role `ChatMessage` carrying results in call order (orphan results with no matching call appended last). This is exactly the positional shape `ConversationMapper.ToParts` round-trips.
- **Decision:** `toolExecutionStart` fires once per distinct callId via `startedCallIds` guard — no duplicate starts if a provider re-emits a complete call. last-write-wins is safe because the loop consumes the stream *downstream* of `FunctionInvokingChatClient`, which surfaces COMPLETE calls (it must, to invoke), not argument fragments.
- 2.3 confirmed: no new mapping code — `ConversationMapper.ToParts` already maps `FunctionCallContent`→`ToolCallPart` and `FunctionResultContent`→`ToolResultPart`; `HandleToolCallAsync` remains event-only (still carries the Group 3 placeholder).
- Tests: `TurnHandlerToolHistoryTests` in `TurnHandlerIntegrationTests.cs` — assistant/tool-role capture, fragmented-call coalescing to one call + one start event, two-tool call-order preservation, round-trip to persisted `ToolCallPart`/`ToolResultPart` (real `FunctionInvokingChatClient` + allow-all permission stub).
- **Reviewer note carried to Group 3:** `TurnEndEvent.ToolResults` still holds the placeholder `{callId,name}` while `_history` holds the real `FunctionResultContent` — two representations of the same calls. Group 3 must reconcile so the wire event reflects the captured result.

## NEXT

- **Up next:** Group 3 — real-result tool events. Emit `toolExecutionEnd` on the matching `FunctionResultContent` (same callId) carrying the real result + `isError`; remove the placeholder `{callId,name}` and `TODO(Group 9.5)` from `HandleToolCallAsync`; accumulate real results into `TurnEndEvent.ToolResults`; handle a call with no observed result (provider/abort) by emitting `toolExecutionEnd` with an error marker rather than fabricating success or hanging.
- **Open questions:** none.
- **Nits / deferred:** null/empty `CallId` collapses to `string.Empty` key (theoretical collision if a provider emits two null-callId calls — not a V1 concern); cross-follow-up isolation covered structurally, not by a dedicated test.
- **Carry-forward:** Group 3 changes the *event* surface in `HandleToolCallAsync`; the history capture from Group 2 is the source of truth for the real results. Decide how `HandleToolCallAsync` (fires on call observation) gets the result that arrives later in the stream — likely emit `toolExecutionEnd` from the result-handling branch keyed by callId, not from the start branch.
