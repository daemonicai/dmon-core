# DEVLOG: typed-command-result-events

Retire the generic `{type:"response", data}` envelope; command results become dedicated typed events correlated by command `id` (ADR-015).

## 1. Protocol foundations (additive)

- Added `ResultEvent : Event` (abstract, `[JsonPropertyName("id")] CommandId`), `CommandErrorEvent`, six `Session*ResultEvent` types, and the `SessionStats` record; registered 7 `[JsonDerivedType]` discriminators on `Event.cs` (`commandError`, `session.{create,fork,clone,load,list,getStats}Result`).
- **Decision (orchestrator):** moved `SessionMeta`/`SessionTokens`/`SessionCost` from `Dmon.Core.Session` to `Dmon.Protocol.Sessions`. `Dmon.Protocol` is a leaf project (no project refs) and the typed session events must reference `SessionMeta`; it's a pure wire-contract DTO so it belongs in the contract layer. Every `[JsonPropertyName]` preserved byte-identically (meta.json/wire unchanged); ~14 referencing files updated. Reviewer confirmed the move is byte-identical and Protocol is still a leaf.
- **Decision:** bumped `ProtocolVersion.Current` `0.1→0.2` *and* `Directory.Build.props` `MinVerMinimumMajorMinor` `0.1→0.2` — the latter is an ADR-011 version-skew guard that compares the MinVer tag's Major.Minor against `ProtocolVersion.Current`; both must move together.
- **Gate hiccup (resolved):** a stale `bin/Debug/dmoncore.dll` (27 May) made the `AgentReady` integration test read `1.0` because `CoreProcessFixture.FindCoreDll()` probes Debug before Release. Fixed by `dotnet clean -c Debug` + rebuild. The Debug-before-Release probe is a **latent harness bug** (flagged by reviewer) — out of scope here; see NEXT.
- Updated four version-pinned tests for the `0.2` bump (`ProtocolVersionTests` ×3, `CoreResolverTests.ResolveAsync_CacheHit_DoesNotCallNetwork`); they now derive the expected version from `ProtocolVersion.Current` so they won't break on the next bump.
- Gates: `make build` 0/0; full `make test` green (all projects, 0 failures, 2 pre-existing skips); `openspec validate --strict` valid.

## 2. Session handler migration + retire ResponseEvent  /  4. Host consumption

(Groups 2 and 4 done together — the core emit changes and Terminal host consumption are inseparable for a green gate.)

- Rewrote all `SessionHandler` emit sites to typed events threading `cmd.Id`: create/fork/clone/load → `Session*ResultEvent{id,session}`, list → `SessionListResultEvent{id,sessions}`, getStats → `SessionStatsResultEvent{id,stats:SessionStats}`. `setName` success still emits `SessionUpdatedEvent`.
- **Decision (D2):** command failures → `CommandErrorEvent{id,command,code,message}` with codes `noActiveSession` / `noSessionIdOrPath` / `sessionLocked`. The locked-session path emits BOTH a correlated `CommandErrorEvent` AND keeps the ambient `ErrorEvent{code:"sessionLocked",recoverable:false}` notification (intentional, per D2 — tests assert both + ordering).
- **Decision (A1, orchestrator ruling):** the `session.getMessages` quarantine is narrowed to its **success payload only**. Its failure path emits `CommandErrorEvent` like everything else. So the *only* remaining `new ResponseEvent` in `src` is `SessionHandler.GetMessagesAsync` success (`SessionHandler.cs:229`); no `ResponseEvent{success:false}` exists anywhere.
- Consequence: the host's `case ResponseEvent when !Success` arm is dead and was removed from `ConsoleEventHandler`; a bare `case ResponseEvent:` remains for the getMessages success payload. `TrackActiveSession` now keys off the typed result events' `SessionMeta`; `CommandErrorEvent` renders `[Failed] {command}: {message}`. `ResponseEvent` doc comment updated to say "getMessages success only".
- **Review:** reviewer caught B1 (a stray `ResponseEvent{success:false}` left in `SetNameAsync`) — fixed to `CommandErrorEvent`; re-audit approved.
- Added wire-shape serialization tests (`SessionResultEventSerializationTests`), `SessionHandlerTypedEventsTests`, and `SessionTypedEventHandlerTests` (host).
- Gates: `make build` 0/0; full `make test` green (Core 558/+1 skip, Terminal 157, Protocol 49, Gateway 123, …, 0 failures, 2 pre-existing skips); `openspec validate --strict` valid.

## 3. Retrofit model/auth result events onto ResultEvent

- Reparented `ModelListResultEvent` + `ModelModelsResultEvent` onto `ResultEvent`. Threaded the real command id: `ModelModelsHandler` sets `CommandId = cmd.Id` (both returns); `ModelListHandler.Handle()` → `Handle(string commandId)`, fed `cmd.Id` from `NullModelHandler.ListAsync`. No fabricated ids.
- **Decision / finding:** the four auth result events (`AuthStatusResultEvent`, `AuthLoginCompleteEvent`, `AuthLogoutCompleteEvent`, `AuthLoginFailedEvent`) are **never emitted in the core** — auth is stubbed (`NullAuthHandler` throws `NotImplementedException`); they're only consumed by the host. So the retrofit is **contract-only**: the types now derive from `ResultEvent` (gain `id`), but there are no emit sites to thread an id through (task 3.3's "no originating command" condition). Did NOT implement auth emission (out of scope) and did NOT fabricate ids. When auth is implemented later, the contract is already id-correlated.
- Fixed `Event.cs` base doc comment (reviewer nit): it now notes `ResultEvent` subclasses carry the originating command `id`, while streaming/notification events don't.
- Tests: model-listing tests assert id correlation (`Handle_ThreadsCommandId`); new `ModelAndAuthResultEventSerializationTests` asserts both model + all four auth events carry `id` and round-trip with correct `type` discriminators.
- **Review:** reviewer approved (2 nits — the Event.cs doc, fixed; and `AuthStatusResultEvent.Providers` being `IReadOnlyList<object>`, pre-existing, deferred to auth implementation).
- Gates: `make build` 0/0; full `make test` green (Protocol 58, Core 559/+1 skip, Terminal 157, Gateway 123, …, 0 failures, 2 pre-existing skips); `openspec validate --strict` valid.

## 5. Finalisation

- Residual sweep: exactly one `new ResponseEvent` remains (`SessionHandler.GetMessagesAsync` success); no `{type:"response"` literal or `Success = false` shapes anywhere; the `"response"` discriminator survives only in `Event.cs` registration + `ResponseEvent.cs` doc.
- Gates: `make build` 0/0; full `make test` green (0 failures, 2 pre-existing skips); `openspec validate --strict` valid.
- **Change is implementation-complete.** All 5 groups ticked.

## NEXT

- **Up next:** nothing in this change — implementation-complete. Orchestrator to propose `/opsx:archive` (awaiting user confirmation). Then apply the stacked `change/conversation-persistence`, which un-quarantines `session.getMessages`.
- **Open questions:** none.
- **Deferred (carried to other work):** (1) `session.getMessages` success on legacy `ResponseEvent` → retired by conversation-persistence. (2) Auth result emission + a typed `AuthStatusResultEvent.Providers` → when auth is implemented. (3) Harness: `CoreProcessFixture.FindCoreDll()` prefers `bin/Debug` over `bin/Release` (`dotnet clean -c Debug` before `make test`).
- **Carry-forward:** branches — `change/typed-command-result-events` (this, complete) ← `change/conversation-persistence` stacked on top.
