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
