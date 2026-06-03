# DEVLOG: conversation-persistence

Session-storage owns a lossless, dmon-owned **parts** conversation record; the memory tier derives its index from it; faithful session resume; write-time attachment offloading; un-quarantines `session.getMessages` (ADR-016).

## 1. Record and part types (additive)

- New `Dmon.Protocol.Conversation` namespace: abstract `SessionLogLine` (`[JsonPolymorphic("type")]`) → `MessageRecord`(`message`, `{entryId,timestamp,role,parts}`) + `CompactionMessage`(`compaction`); abstract `Part` (`[JsonPolymorphic("type")]`) → `text`/`toolCall`/`toolResult`/`image`/`reasoning`/`usage`/`unknown`. `args`/`result`/`raw` are `JsonElement` (BCL; intrinsic free-form, not a third-party type).
- **Prereq (orchestrator-sanctioned):** moved `CompactionMessage` from `Dmon.Core.Session` into the union (`: SessionLogLine`). Removed its manual `Type="compaction"` property; preserved the wire shape by changing `MessageAppender.AppendCompactionAsync` to serialize **through the base** (`Serialize<SessionLogLine>(compaction)`) so `"type":"compaction"` is still emitted. Namespace updates in `SessionStore` + 2 tests; compaction tests assert the on-wire `type` and stay green.
- **Note (read path):** `SessionStore` still deserializes a compaction line directly to the concrete `CompactionMessage` — works because STJ ignores the unmapped `type` by default. On record for when the read path is reworked (groups 3–5).
- **Decision:** added a `JsonSchemaExporter` test (`ConversationRecordSchemaTests`, 6 assertions) that exports schemas for `SessionLogLine` + `Part`, asserts success, and asserts the schema JSON contains **no** `Microsoft.Extensions.AI`/`ChatMessage`/`AIContent` — locking the "no third-party types in the API" principle as an automated guard rather than inspection. (`JsonSchemaExporter` on .NET 10 needs an explicit `DefaultJsonTypeInfoResolver` in the options.)
- **Review:** reviewer approved (no blockers; nits: the schema-export check — addressed via the new test; minimal `UsagePart` and doc-comment value sets for `role`/`reason` — accepted/deferred).
- Gates: `make build` 0/0; full `make test` green (Protocol 64, Core 559/+1 skip, Terminal 157, Gateway 123, …, 0 failures, 2 pre-existing skips); `openspec validate --strict` valid.

## 2. ChatMessage ⇆ record mapping

- New `ConversationMapper` (static, `Dmon.Core.Session`, `src/Dmon.Core/Session/ConversationMapper.cs`) — the single bidirectional bridge between M.E.AI `ChatMessage`/`AIContent` and the dmon-owned parts record. Placed in `Dmon.Core` (which references both M.E.AI and `Dmon.Protocol`); never in `Dmon.Protocol`, keeping the API definition third-party-free (ADR-016).
- **API:** `ToParts(ChatMessage, string producedBy="unknown") → (string Role, IReadOnlyList<Part>)` (write) and `ToMessage(MessageRecord) → ChatMessage` (replay).
- **Decision (entryId/timestamp ownership):** `ToParts` returns only `(role, parts)` and mints **no** `entryId`/`timestamp` — those belong to session-storage at append time (design D2, group 3). The mapper is content-only, so the round-trip test never depends on synthesised ids.
- **Write (D1):** `TextContent`→`TextPart`, `FunctionCallContent`→`ToolCallPart{callId,name,args}`, `FunctionResultContent`→`ToolResultPart{callId,result}` **inline** (`attachmentRef` null — offloading is group 3, D4); any other `AIContent`→`UnknownPart{raw,producedBy}`. Lenient: `MapUnknown` wraps its own `SerializeToElement` in try/catch with a type-name fallback so nothing throws or drops. `args`/`result` via `JsonSerializer.SerializeToElement` (default options, matching `MessageAppender`).
- **Replay subset (D1):** `ToMessage` reconstructs only `text`/`toolCall`/`toolResult`/`image`; `reasoning`/`usage`/`unknown` are preserved in the record but excluded from LLM context. `image` is best-effort (inline `dataBase64`→`DataContent`; `attachmentRef`-only skipped — file IO is groups 3/5).
- **Tests** (`test/Dmon.Core.Tests/Session/ConversationMapperTests.cs`, 6 facts): value-level round-trip for text/toolCall(args `"ls -la"`)/toolResult(`"file listing output"`); `UnknownPart` preserved on write + excluded on replay; reasoning/usage excluded on replay; serialized `MessageRecord` JSON asserted free of `Microsoft.Extensions.AI`/`ChatMessage`/`AIContent` and concrete content type names; structural confirmation `ToParts` exposes no id/timestamp.
- **Review:** reviewer approved with nits; addressed nit 1 (round-trip now asserts the actual arg/result *values*, not just presence) and nit 3 (removed the four `// ---` section-banner comments). Deferred nits 2 (`producedBy` default never falls back to type name — judgement call) and 4 (image/`result==null` replay branches uncovered — dormant forward-compat).
- Gates: `make build` 0/0; `make test` green (Core 565/+1 pre-existing skip, Protocol/Memory/Terminal/Gateway/BuiltinTools/Extensions/Runtime all pass, 0 failures); `openspec validate --strict` valid.

## NEXT

- **Up next:** Group 3 — session-storage canonical write path: canonical append API minting+returning `entryId`, write-time attachment offloading (`Daemon:Session:AttachmentThresholdBytes` → `attachments/<callId>`, `ToolResultPart{preview/result, attachmentRef}`), and compaction-aware typed read-back returning the parts records. Will consume `ConversationMapper.ToParts`. Not yet wired into the turn flow (that's group 4).
- **Open questions:** none.
- **Nits / deferred:** compaction read path still bypasses the polymorphic base (works today) — revisit in the group-3 read-back rework. `ToParts` `producedBy` default never reaches the type-name fallback; image/offloaded-result replay branches uncovered until offloading lands (group 3).
- **Carry-forward:** branch `change/conversation-persistence`. Apply gates: `dotnet clean -c Debug` before `make test` (stale-`bin/Debug` hazard). Group 7 un-quarantines `session.getMessages`.
