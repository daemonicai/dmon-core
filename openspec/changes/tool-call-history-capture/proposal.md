## Why

Tools already execute during a turn (M.E.AI's `FunctionInvokingChatClient` invokes the registered `AIFunction`s with `ChatOptions.Tools` populated), but `TurnHandler`'s streaming loop captures **only the assistant's accumulated text** into `_history`. The structured `FunctionCallContent` (the tool call) and `FunctionResultContent` (the result) are streamed past and discarded. Consequently: `toolExecutionEnd` carries a **placeholder** result rather than the real one (the `TODO(Group 9.5)` in `HandleToolCallAsync`), and tool calls/results never reach the conversation-persistence machinery — so they are absent from the persisted log, from faithful resume, and from subsequent turns' context. The conversation-persistence change (just completed) built the entire parts-model pipeline (`ToolCallPart`/`ToolResultPart`, write-time offloading, D6 reconciliation, replay) to carry exactly this content; this change feeds it. It also closes the deferred security item that becomes live the moment real `callId`s reach the offload path.

## What Changes

- **Capture structured tool content into `_history`:** the turn loop records the assistant message's `FunctionCallContent`(s) and the corresponding `FunctionResultContent`(s) into the conversation history, so they flow through the existing `ConversationMapper` → canonical parts record → persistence → replay path. After a turn with tool use, `_history` (and `messages.jsonl`) contains the tool call and result as typed parts, not just final text.
- **Emit real tool results in events:** `toolExecutionEnd` (and the `TurnEndEvent.ToolResults` accumulation) carry the actual `FunctionResultContent` value and real `isError` state, replacing the placeholder `{callId, name}` object. The `TODO(Group 9.5)` in `TurnHandler.HandleToolCallAsync` is resolved.
- **Guard `AttachmentStore` against `callId` path traversal:** before a live tool-call id is used to build an attachment filename, sanitise/validate it so a `callId` containing path separators or `..` cannot escape the session's `attachments/` directory. Reject or safely encode out-of-bounds ids.
- **No change to tool execution itself:** tools continue to execute via `FunctionInvokingChatClient` under the permission gate. This change is about *capturing and surfacing* what execution already produces, not about how tools run.

## Capabilities

### New Capabilities
<!-- None — this change enriches existing capabilities; it introduces no new one. -->

### Modified Capabilities
- `agent-core`: the **Turn execution loop** requirement gains the obligation that structured tool calls and tool results are recorded into the conversation history (so they are persisted and replayed), and that `toolExecutionEnd` carries the real tool result rather than a placeholder. The "Turn with tool calls" scenario is strengthened accordingly.
- `attachment-offloading`: a new requirement that the attachment write path sanitises/validates `callId` so it cannot be used for path traversal out of `attachments/`. (This capability was reshaped to write-time offloading by `conversation-persistence`; this delta layers the security guard onto that owner.)

## Impact

- **Code:** `src/Dmon.Core/Rpc/TurnHandler.cs` — the streaming loop captures `FunctionCallContent`/`FunctionResultContent` into `_history` and threads the real result through `HandleToolCallAsync`; the placeholder is removed. `src/Dmon.Core/Session/AttachmentStore.cs` — `callId` sanitisation/validation before filename construction. No new wire types (tool parts already exist from `conversation-persistence`); `toolExecutionEnd`/`TurnEndEvent` payloads now carry real result data.
- **Specs:** `agent-core` and `attachment-offloading` deltas in this change.
- **ADRs:** no new ADR. Consistent with ADR-001 (M.E.AI internal — tool content is mapped to dmon-owned parts before persistence), ADR-006 (tool execution stays behind the permission gate), and ADR-016 (the parts record is the canonical home for tool calls/results).
- **Dependency / ordering:** depends on `conversation-persistence` (the parts model, `ConversationMapper`, write-time offloading, D6 reconciliation, and faithful resume). That change's code is present on this branch; its specs should be archived first so the standing `agent-core`/`attachment-offloading`/`session-storage` specs reflect the parts model this change builds on.
- **No migration:** no production data; behavioural change only (historical turns now include tool parts going forward).
</content>
</invoke>
