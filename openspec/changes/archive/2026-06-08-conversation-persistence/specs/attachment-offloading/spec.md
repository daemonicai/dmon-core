## MODIFIED Requirements

### Requirement: Attachment offloading is transparent to tools
Tools SHALL return raw strings. Offloading SHALL be performed by **session-storage at persistence time**, which SHALL be the only component aware of `IAttachmentStore`. Individual tool implementations SHALL NOT take an `IAttachmentStore` dependency, and no `IChatClient` middleware SHALL perform offloading.

#### Scenario: Tool implementation has no attachment dependency
- **WHEN** any built-in tool class is inspected
- **THEN** its constructor and method signatures contain no reference to `IAttachmentStore` or attachment paths

#### Scenario: Offloading is owned by the persistence write path
- **WHEN** a large tool result is recorded
- **THEN** session-storage (not a pipeline chat client) writes the attachment and records `attachmentRef` + `preview` on the `toolResult` part

## REMOVED Requirements

### Requirement: AttachmentOffloadingChatClient offloads large tool results
**Reason:** Offloading moves to persistence time per ADR-016; the in-flight turn now sees the full tool result it requested, and offloading is performed once when the turn is written. The `AttachmentOffloadingChatClient` middleware is deleted.
**Migration:** Session-storage performs threshold-based offloading on append (`session-storage` capability, "Attachment threshold"); historical results are served in preview form via record→`ChatMessage` replay.

### Requirement: Offloading is skipped when no session is active
**Reason:** Offloading now occurs only on the persistence write path, which always runs within a loaded session, so the "no active session" guard is moot.
**Migration:** N/A — persistence implies an active session.

### Requirement: Pipeline position — after FunctionInvokingChatClient, before RetryingChatClient
**Reason:** The `AttachmentOffloadingChatClient` middleware is removed, so it has no pipeline position. The turn pipeline becomes `PermissionGateChatClient → FunctionInvokingChatClient → RetryingChatClient → provider`.
**Migration:** N/A — offloading is no longer a pipeline concern.
