# DEVLOG: tool-call-history-capture

<!-- Capture structured tool calls/results into history (persist + replay) and emit real tool-result events; guard AttachmentStore callId against path traversal. -->

## 1. AttachmentStore callId path-traversal guard

- `AttachmentStore.StoreIfLargeAsync` now validates `callId` before building the filename. Safe ids (allowlist `[A-Za-z0-9._-]`, non-empty, no `..` segment) are used verbatim as `attachments/<callId>.<ext>`; unsafe ids derive a deterministic `unsafe_<sha256-hex>` filename.
- **Decision:** SHA-256 hex of the raw callId for unsafe ids — collision-resistant (rejects the lossy `Replace` the design's Risks section calls out) and always filename-safe. Alternative reversible-escape rejected as more code for no benefit.
- **Decision:** never throw on an unsafe id (design D4 — persistence is best-effort; a hostile provider id must not abort a turn or lose the result). The `InvalidOperationException` after the full-path containment check is defensive-only and provably unreachable (derived names are separator-free; the allowlist excludes separators).
- Belt-and-suspenders: explicit `..` rejection is load-bearing because both dots are in the allowlist; the post-`Path.Combine` `GetFullPath` + trailing-separator boundary check is the real backstop and catches the sibling-prefix (`attachments-evil/`) false positive.
- Tests in `test/Dmon.Core.Tests/Session/AttachmentStoreTests.cs` cover the three spec scenarios, incl. a negative assertion that no file exists at the `../../etc/evil.txt` traversal target.
- **Note:** repo HAS a formal test project (`test/Dmon.Core.Tests/`) — the older "scaffold under sandbox/" guidance is stale.

## NEXT

- **Up next:** Group 2 — capture `FunctionCallContent`/`FunctionResultContent` in `TurnHandler.RunTurnAsync`'s streaming loop and append history in M.E.AI-canonical shape (assistant text+toolCalls, then tool-role results) so the existing `ConversationMapper`/persistence/replay path round-trips them. Replace the text-only `_history.Add(new ChatMessage(ChatRole.Assistant, fullText))`.
- **Open questions:** none.
- **Nits / deferred:** none outstanding (reviewer nits applied).
- **Carry-forward:** Group 1 committed. `ConversationMapper.MapFunctionResult` currently hardcodes `IsError = false` — that is conversation-persistence's existing mapper and out of scope to change here (Group 2.3 forbids new mapping code); event-level `isError` is Group 3's concern.
