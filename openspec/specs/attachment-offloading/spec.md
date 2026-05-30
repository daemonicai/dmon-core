## Purpose

Define the `AttachmentOffloadingChatClient` middleware that intercepts large tool results in the pipeline, writes them to the session's `attachments/` directory, and replaces the inline content with a compact JSON reference, keeping `messages.jsonl` small while preserving full output fidelity.

## Requirements

### Requirement: AttachmentOffloadingChatClient offloads large tool results
The system SHALL provide an `AttachmentOffloadingChatClient` `IChatClient` middleware. When a `FunctionResultContent` in the pipeline response exceeds `Daemon:Session:AttachmentThresholdBytes` (default 1024 bytes), the middleware SHALL write the full content to `attachments/<callId>.txt` in the current session directory and replace the result string with a compact JSON object: `{"attachmentPath": "attachments/<callId>.txt", "preview": "<first 200 chars>..."}`.

#### Scenario: Result below threshold passes through unchanged
- **WHEN** a `FunctionResultContent` result string is shorter than the configured threshold
- **THEN** the middleware passes it through without modification

#### Scenario: Result above threshold is offloaded
- **WHEN** a `FunctionResultContent` result string is longer than the configured threshold
- **THEN** the middleware writes the full content to `attachments/<callId>.txt` in the session directory and replaces the result with `{"attachmentPath": "attachments/<callId>.txt", "preview": "<first 200 chars>..."}`

#### Scenario: Preview is truncated at 200 characters
- **WHEN** a large result is offloaded
- **THEN** the `"preview"` field contains at most 200 characters of the original content

### Requirement: Attachment offloading is transparent to tools
Tools SHALL return raw strings. The `AttachmentOffloadingChatClient` SHALL be the only component aware of `IAttachmentStore`. Individual tool implementations SHALL NOT take an `IAttachmentStore` dependency.

#### Scenario: Tool implementation has no attachment dependency
- **WHEN** any built-in tool class is inspected
- **THEN** its constructor and method signatures contain no reference to `IAttachmentStore` or attachment paths

### Requirement: Offloading is skipped when no session is active
If no session is currently loaded, the middleware SHALL pass results through unchanged rather than throwing or silently discarding content.

#### Scenario: No active session
- **WHEN** `AttachmentOffloadingChatClient` processes a large result and `ISessionHandler.CurrentSession` is null
- **THEN** the full result string is passed through to the LLM unchanged (no offload attempted)

### Requirement: Pipeline position — after FunctionInvokingChatClient, before RetryingChatClient
`AttachmentOffloadingChatClient` SHALL be inserted in the pipeline between `FunctionInvokingChatClient` and `RetryingChatClient` so that tool results are available for inspection before they reach the provider client.

#### Scenario: Pipeline order is correct
- **WHEN** the turn pipeline is assembled in `TurnHandler`
- **THEN** the order is: `PermissionGateChatClient → FunctionInvokingChatClient → AttachmentOffloadingChatClient → RetryingChatClient → provider`
