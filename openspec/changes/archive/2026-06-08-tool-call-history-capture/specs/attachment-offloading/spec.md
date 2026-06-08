## ADDED Requirements

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
</content>
