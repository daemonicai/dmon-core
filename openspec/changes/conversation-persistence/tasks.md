## 1. Record and part types (additive)

- [x] 1.1 Define the log-line union in `src/Dmon.Protocol` (or session-storage record namespace): `message {entryId, timestamp, role, parts}` and the existing `compaction`, discriminated by `type` via `[JsonPolymorphic]`.
- [x] 1.2 Define the `Part` union discriminated by `type`: `TextPart`, `ToolCallPart {callId, name, args: JsonElement}`, `ToolResultPart {callId, result?: JsonElement, attachmentRef?, isError, truncated?}`, `ImagePart {mediaType, attachmentRef|dataBase64}`, `ReasoningPart {text}`, `UsagePart`, `UnknownPart {raw: JsonElement, producedBy}`.
- [x] 1.3 Register all part types and log-line types on their `[JsonDerivedType]` tables; confirm `JsonSchemaExporter` describes the record (no third-party type names in the schema).
- [x] 1.4 Build green (additive; nothing consumes the types yet).

## 2. ChatMessage ⇆ record mapping

- [x] 2.1 Write `ChatMessage → record` mapping: `TextContent`→`TextPart`, `FunctionCallContent`→`ToolCallPart`, `FunctionResultContent`→`ToolResultPart`; unrecognised `AIContent`→`UnknownPart {raw, producedBy}` (lenient, never throw/drop).
- [x] 2.2 Write `record → ChatMessage` replay-subset mapping: reconstruct only `text`/`toolCall`/`toolResult`/`image`; skip `reasoning`/`usage`/`unknown`.
- [x] 2.3 Unit tests: round-trip for known parts; unknown content preserved opaquely and excluded from replay; no `Microsoft.Extensions.AI` type appears in serialized output.
- [x] 2.4 Build and tests green.

## 3. Session-storage canonical write path

- [ ] 3.1 Add the canonical append API to session-storage: serialize a `message` record, mint and return its `entryId`, append LF-terminated line to `messages.jsonl`.
- [ ] 3.2 Implement write-time attachment offloading: tool result over `Daemon:Session:AttachmentThresholdBytes` → full content to `attachments/<callId>`, `ToolResultPart {preview, attachmentRef}`; else inline `result`.
- [ ] 3.3 Add typed read-back returning the log records (parts model), compaction-aware.
- [ ] 3.4 Tests for append + offloading threshold + typed read-back. Build and tests green (new API not yet wired into the turn flow).

## 4. Invert turn-completion wiring (cutover)

- [ ] 4.1 At turn completion, append via session-storage **first** (minting `entryId`), then call `IMemory.RecordAsync(turns)` passing the minted `entryId` for index-only ingestion.
- [ ] 4.2 Strip canonical-JSONL writing and id-minting from `ShortTermMemory.RecordAsync` (index-only, keyed on supplied `entryId`).
- [ ] 4.3 Update `ShortTermMemory.RebuildFromJsonlAsync` to parse the parts record and derive index text from `TextPart`s.
- [ ] 4.4 Delete `TurnLineRecord`.
- [ ] 4.5 Update memory/session-storage tests for the new ownership. Build and tests green.

## 5. Faithful session resume

- [ ] 5.1 On `session.load`, read the canonical record via session-storage, apply the compaction rule, and seed `TurnHandler._history` via the replay-subset mapping.
- [ ] 5.2 After each completed turn is persisted, reconcile that turn's `_history` entries to the persisted preview form (D6) so subsequent live turns send shrunk historical tool results.
- [ ] 5.3 Tests: resume restores tool-call/result context; reasoning/usage/unknown excluded from context; compaction honoured. Build and tests green.

## 6. Remove the provider-input offloading middleware

- [ ] 6.1 Delete `AttachmentOffloadingChatClient` and remove it from the turn pipeline assembly (`PermissionGateChatClient → FunctionInvokingChatClient → RetryingChatClient → provider`).
- [ ] 6.2 Remove/retarget its tests; confirm the in-flight turn now sees full tool results and historical turns use preview form. Build and tests green.

## 7. Un-quarantine session.getMessages

- [ ] 7.1 Add `SessionMessagesResultEvent : ResultEvent` (from the typed-command-result-events change) carrying the log records (parts model).
- [ ] 7.2 Convert `session.getMessages` from the legacy quarantined path to emit `SessionMessagesResultEvent`; remove the last `ResponseEvent` usage.
- [ ] 7.3 Update host/RPC consumers and tests for the typed event. Build and tests green.

## 8. Finalisation

- [ ] 8.1 Grep the solution: no `TurnLineRecord`, no `AttachmentOffloadingChatClient`, no residual `ResponseEvent`; no `Microsoft.Extensions.AI` type in any persisted-record or wire schema.
- [ ] 8.2 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 8.3 `make test` (or `dotnet test -c Release`) green across all projects.
- [ ] 8.4 `openspec validate conversation-persistence --strict` passes.
