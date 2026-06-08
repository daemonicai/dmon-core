## Purpose

Define how large tool results are offloaded to the session's `attachments/` directory at persistence time, keeping `messages.jsonl` small while preserving full output fidelity. Offloading is owned by session-storage on the write path (ADR-016); there is no pipeline middleware involved.

## Requirements

### Requirement: Attachment offloading is transparent to tools
Tools SHALL return raw strings. Offloading SHALL be performed by **session-storage at persistence time**, which SHALL be the only component aware of `IAttachmentStore`. Individual tool implementations SHALL NOT take an `IAttachmentStore` dependency, and no `IChatClient` middleware SHALL perform offloading.

#### Scenario: Tool implementation has no attachment dependency
- **WHEN** any built-in tool class is inspected
- **THEN** its constructor and method signatures contain no reference to `IAttachmentStore` or attachment paths

#### Scenario: Offloading is owned by the persistence write path
- **WHEN** a large tool result is recorded
- **THEN** session-storage (not a pipeline chat client) writes the attachment and records `attachmentRef` + `preview` on the `toolResult` part

### Requirement: Attachment writes are confined to the session attachments directory
When the attachment write path builds an attachment filename from a tool call's `callId`, it SHALL ensure the resulting file is written inside the active session's `attachments/` directory and nowhere else. A `callId` that is empty, contains a path separator (`/` or `\`), contains `..`, or otherwise could escape `attachments/` SHALL NOT be used verbatim as a filename; instead a deterministic, collision-resistant safe filename SHALL be derived so that distinct `callId`s map to distinct files and the returned `attachmentRef` still resolves to the written file. The attachment write SHALL never resolve to a path outside `attachments/`.

#### Scenario: Safe callId is used directly
- **WHEN** a tool result is offloaded for a `callId` containing only filename-safe characters
- **THEN** the file is written to `attachments/<callId>.<ext>` and the `attachmentRef` references it

#### Scenario: Path-traversal callId cannot escape the attachments directory
- **WHEN** a tool result is offloaded for a `callId` such as `../../etc/evil` (containing `..` and path separators)
- **THEN** the file is written inside the session's `attachments/` directory under a derived safe filename, and no file is created outside `attachments/`

#### Scenario: Distinct unsafe callIds do not collide
- **WHEN** two different `callId`s that both require sanitisation are offloaded in the same session
- **THEN** they are written to distinct files and each `attachmentRef` resolves to its own content
