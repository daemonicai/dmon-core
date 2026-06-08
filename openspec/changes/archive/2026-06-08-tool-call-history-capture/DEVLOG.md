# DEVLOG: tool-call-history-capture

<!-- Capture structured tool calls/results into history (persist + replay) and emit real tool-result events; guard AttachmentStore callId against path traversal. -->

## 1. AttachmentStore callId path-traversal guard

- `AttachmentStore.StoreIfLargeAsync` now validates `callId` before building the filename. Safe ids (allowlist `[A-Za-z0-9._-]`, non-empty, no `..` segment) are used verbatim as `attachments/<callId>.<ext>`; unsafe ids derive a deterministic `unsafe_<sha256-hex>` filename.
- **Decision:** SHA-256 hex of the raw callId for unsafe ids — collision-resistant (rejects the lossy `Replace` the design's Risks section calls out) and always filename-safe. Alternative reversible-escape rejected as more code for no benefit.
- **Decision:** never throw on an unsafe id (design D4 — persistence is best-effort; a hostile provider id must not abort a turn or lose the result). The `InvalidOperationException` after the full-path containment check is defensive-only and provably unreachable (derived names are separator-free; the allowlist excludes separators).
- Belt-and-suspenders: explicit `..` rejection is load-bearing because both dots are in the allowlist; the post-`Path.Combine` `GetFullPath` + trailing-separator boundary check is the real backstop and catches the sibling-prefix (`attachments-evil/`) false positive.
- Tests in `test/Dmon.Core.Tests/Session/AttachmentStoreTests.cs` cover the three spec scenarios, incl. a negative assertion that no file exists at the `../../etc/evil.txt` traversal target.
- **Note:** repo HAS a formal test project (`test/Dmon.Core.Tests/`) — the older "scaffold under sandbox/" guidance is stale.

## 2. Capture structured tool content into history

- `RunTurnAsync` streaming loop now accumulates, per `while(true)` iteration: `accumulatedCalls` (callId→`FunctionCallContent`, last-write-wins), `callOrder` (first-seen order), `accumulatedResults` (callId→`FunctionResultContent`), `startedCallIds` (HashSet). Still `GetStreamingResponseAsync` — no switch to non-streaming.
- **Decision:** accumulators declared INSIDE the `while(true)` loop body so each follow-up iteration (`continue`) gets fresh state — prevents a prior iteration's calls leaking into a later follow-up's assistant message.
- End-of-turn append (replaces text-only add): assistant `ChatMessage` = `[TextContent(fullText), ...calls in callOrder]`, then (only if results non-empty) a tool-role `ChatMessage` carrying results in call order (orphan results with no matching call appended last). This is exactly the positional shape `ConversationMapper.ToParts` round-trips.
- **Decision:** `toolExecutionStart` fires once per distinct callId via `startedCallIds` guard — no duplicate starts if a provider re-emits a complete call. last-write-wins is safe because the loop consumes the stream *downstream* of `FunctionInvokingChatClient`, which surfaces COMPLETE calls (it must, to invoke), not argument fragments.
- 2.3 confirmed: no new mapping code — `ConversationMapper.ToParts` already maps `FunctionCallContent`→`ToolCallPart` and `FunctionResultContent`→`ToolResultPart`; `HandleToolCallAsync` remains event-only (still carries the Group 3 placeholder).
- Tests: `TurnHandlerToolHistoryTests` in `TurnHandlerIntegrationTests.cs` — assistant/tool-role capture, fragmented-call coalescing to one call + one start event, two-tool call-order preservation, round-trip to persisted `ToolCallPart`/`ToolResultPart` (real `FunctionInvokingChatClient` + allow-all permission stub).
- **Reviewer note carried to Group 3:** `TurnEndEvent.ToolResults` still holds the placeholder `{callId,name}` while `_history` holds the real `FunctionResultContent` — two representations of the same calls. Group 3 must reconcile so the wire event reflects the captured result.

## 3. Real-result tool events

- `HandleToolCallAsync` (placeholder + `TODO(Group 9.5)`) deleted. Split into `EmitToolExecutionStartAsync` (fires once per distinct callId on first `FunctionCallContent`, guarded by `startedCallIds`) and `EmitToolExecutionEndAsync` (fires on the matching `FunctionResultContent`, guarded by `endedCallIds`).
- **Decision:** `isError = fr.Exception is not null` (M.E.AI `FunctionResultContent.Exception` is set when the invoked `AIFunction` threw; FICC populates it). Wire `result` = `fr.Result` on success (the tool's own object — no M.E.AI type, ADR-001), or `new { error = fr.Exception.Message }` on error; `fr.Result ?? new {}` for a null successful return. `TurnEndEvent.ToolResults` now carries these real results — placeholder gone.
- Exactly-one-end per callId: result branch and sweep share the `endedCallIds` set, so neither can double-end.
- **Anomaly sweep (`EmitMissingToolEndEventsAsync`, task 3.3):** runs at end-of-stream and in the `catch(OperationCanceledException)` block (both with `CancellationToken.None`), emitting an error-marker end (`{ error = "Tool result not received from provider." }`, isError=true) for any started-but-unended callId.
- **Key finding (reviewer-confirmed):** `PermissionGateChatClient.GetStreamingResponseAsync` FULLY BUFFERS the inner stream before yielding, so every `FunctionCallContent` reaches `TurnHandler` already paired with its `FunctionResultContent` (real, FICC-synthesised for unknown tools, or the Deny-path result). Therefore `startedCallIds == endedCallIds` at normal completion and the sweep is **defensive code unreachable through today's pipeline** — kept as the binding D3 guarantee for a future non-buffering middleware. Covered by a **direct unit test** of `EmitMissingToolEndEventsAsync` (method made `internal`; IVT to `Dmon.Core.Tests` already existed) since it can't be driven end-to-end.
- **Hang during development:** the first orphan test hung — root cause was `PermissionGateChatClient` defaulting to `PermissionResult.Prompt` for an unregistered tool (no extension owns the name) → awaits a confirm that never arrives. Fixed by giving the orphan stub a `_callCount` guard and using an allow-all tool registry; all test runs now use `--blame-hang-timeout` and the anomaly test has `[Fact(Timeout=…)]`.
- Tests (`TurnHandlerRealToolResultTests`): real-result in `toolExecutionEnd` + `TurnEndEvent.ToolResults` (no placeholder), throwing-tool → isError=true, orphan/FICC-synthesis path (exactly one start+end), aborted-turn completes without hang, and the direct sweep marker-emission unit test.

## NEXT

- **Up next:** Group 4 — tests & finalisation (4.1 persist+resume integration test for tool parts; 4.2 real-result/`isError` assertions [largely covered in Group 3 — confirm/augment]; 4.3 large tool result offloaded at write-time to `attachments/<safe-callId>` + D6 preview replay; 4.4 grep gates; 4.5 build; 4.6 full test; 4.7 validate).
- **DECISION NEEDED (raise with user before Group 4/archive):** spec `agent-core` "Turn abort" scenario says `stopReason: aborted`, but the code emits `"cancelled"` (pre-existing, not introduced by Group 3; the abort test asserts `"cancelled"`). Either align code→spec (emit `aborted`) or correct the spec wording. Out of Group 3's scope but inside this change's spec surface.
- **Nits / deferred:** none outstanding from Group 3 (all reviewer nits applied).
- **Carry-forward:** the sweep is intentional defensive code; don't "simplify" it away. The PermissionGate Prompt-vs-Deny default for unregistered tools is a real product observation (reviewer suggested it should likely be `Deny`) — out of scope for this change; do not fix here.
