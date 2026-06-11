## 1. Core fix and regression test

- [x] 1.1 In `src/Dmon.Core/Rpc/SessionHandler.cs`, `CreateAsync`: after `_store.CreateAsync(...)` returns and before emitting `SessionCreatedResultEvent`, set `_currentSession = meta`. Do not acquire the session lock (decision (a)).
- [x] 1.2 Add a regression test in `test/Dmon.Core.Tests` that drives `SessionHandler.CreateAsync` then `LoadAsync` with a path-less `SessionLoadCommand`, and asserts a `SessionLoadedResultEvent` is emitted for the created session id (and that no `CommandErrorEvent code=noSessionIdOrPath` is emitted).
- [x] 1.3 Add/confirm a test asserting that a path-less `session.load` with **no** preceding `create` and no active session still fails with `commandError code=noSessionIdOrPath` (decision (a) does not change this).
- [x] 1.4 Gates: `make build` clean (TreatWarningsAsErrors), `make test` green, `openspec validate gateway-create-activates-session --strict`.
