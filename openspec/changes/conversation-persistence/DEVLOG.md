# DEVLOG: conversation-persistence

Session-storage owns a lossless, dmon-owned **parts** conversation record; the memory tier derives its index from it; faithful session resume; write-time attachment offloading; un-quarantines `session.getMessages` (ADR-016).

## 1. Record and part types (additive)

- New `Dmon.Protocol.Conversation` namespace: abstract `SessionLogLine` (`[JsonPolymorphic("type")]`) → `MessageRecord`(`message`, `{entryId,timestamp,role,parts}`) + `CompactionMessage`(`compaction`); abstract `Part` (`[JsonPolymorphic("type")]`) → `text`/`toolCall`/`toolResult`/`image`/`reasoning`/`usage`/`unknown`. `args`/`result`/`raw` are `JsonElement` (BCL; intrinsic free-form, not a third-party type).
- **Prereq (orchestrator-sanctioned):** moved `CompactionMessage` from `Dmon.Core.Session` into the union (`: SessionLogLine`). Removed its manual `Type="compaction"` property; preserved the wire shape by changing `MessageAppender.AppendCompactionAsync` to serialize **through the base** (`Serialize<SessionLogLine>(compaction)`) so `"type":"compaction"` is still emitted. Namespace updates in `SessionStore` + 2 tests; compaction tests assert the on-wire `type` and stay green.
- **Note (read path):** `SessionStore` still deserializes a compaction line directly to the concrete `CompactionMessage` — works because STJ ignores the unmapped `type` by default. On record for when the read path is reworked (groups 3–5).
- **Decision:** added a `JsonSchemaExporter` test (`ConversationRecordSchemaTests`, 6 assertions) that exports schemas for `SessionLogLine` + `Part`, asserts success, and asserts the schema JSON contains **no** `Microsoft.Extensions.AI`/`ChatMessage`/`AIContent` — locking the "no third-party types in the API" principle as an automated guard rather than inspection. (`JsonSchemaExporter` on .NET 10 needs an explicit `DefaultJsonTypeInfoResolver` in the options.)
- **Review:** reviewer approved (no blockers; nits: the schema-export check — addressed via the new test; minimal `UsagePart` and doc-comment value sets for `role`/`reason` — accepted/deferred).
- Gates: `make build` 0/0; full `make test` green (Protocol 64, Core 559/+1 skip, Terminal 157, Gateway 123, …, 0 failures, 2 pre-existing skips); `openspec validate --strict` valid.

## NEXT

- **Up next:** Group 2 — `ChatMessage ⇆ record` mapping: write side (`TextContent`→`TextPart`, `FunctionCallContent`→`ToolCallPart`, `FunctionResultContent`→`ToolResultPart`; unrecognised `AIContent`→lenient `UnknownPart`), and the replay-subset read side (reconstruct only `text`/`toolCall`/`toolResult`/`image`; skip `reasoning`/`usage`/`unknown`). Plus round-trip + no-third-party-in-output tests.
- **Open questions:** none.
- **Nits / deferred:** the compaction read path bypasses the polymorphic base (works today); revisit when the canonical read path is built (groups 3–5).
- **Carry-forward:** branch `change/conversation-persistence` on `main` (97c0073). Apply gates: `dotnet clean -c Debug` before `make test` (stale-`bin/Debug` hazard). Group 7 un-quarantines `session.getMessages`.
