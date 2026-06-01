## 1. Protocol foundations (additive)

- [ ] 1.1 Add `abstract record ResultEvent : Event` in `src/Dmon.Protocol/Events` with `[JsonPropertyName("id")] CommandId` (required).
- [ ] 1.2 Add `CommandErrorEvent : ResultEvent { command, code, message }` (discriminator `commandError`).
- [ ] 1.3 Add `SessionStats` record (`tokens`, `cost`, `contextUsage`, `currentModel`) in `src/Dmon.Protocol`.
- [ ] 1.4 Add the session result events: `SessionCreatedResultEvent`, `SessionForkedResultEvent`, `SessionClonedResultEvent`, `SessionLoadedResultEvent` (each carrying `SessionMeta`), `SessionListResultEvent { sessions: SessionMeta[] }`, `SessionStatsResultEvent { stats: SessionStats }` — all `: ResultEvent`.
- [ ] 1.5 Register every new event on the `Event` `[JsonDerivedType]` table with `<command>Result` discriminators (`session.createResult`, `session.forkResult`, `session.cloneResult`, `session.loadResult`, `session.listResult`, `session.getStatsResult`, `commandError`).
- [ ] 1.6 Bump `ProtocolVersion.Current` to `"0.2"`.
- [ ] 1.7 Build is green (additive change; nothing consumes the new types yet).

## 2. Session handler migration + retire ResponseEvent

- [ ] 2.1 Rewrite `SessionHandler` emit sites for `create`/`fork`/`clone`/`load`/`list`/`getStats` to emit the corresponding typed result events, threading `cmd.Id` into each event's `id`.
- [ ] 2.2 Replace `ResponseEvent{success:false}` command-failure paths with `CommandErrorEvent` (carrying `cmd.Id`, the command name, a `code`, and the message). Keep the `sessionLocked` `ErrorEvent` notification where it is a genuine ambient notification (per design D2).
- [ ] 2.3 Quarantine `session.getMessages`: retain a single transitional untyped path for this one command only (keep `ResponseEvent` solely for `session.getMessages`, marked transitional in an XML doc comment referencing the turn-persistence dependency).
- [ ] 2.4 Remove `ResponseEvent` usage everywhere except the quarantined `session.getMessages` path; delete now-dead `ResponseEvent` members/discriminators that no longer apply.
- [ ] 2.5 Update protocol-serialization and `SessionHandler` tests to assert the typed events and `commandError`; add a wire-shape assertion for `session.listResult` (`{type,id,sessions}`) and `session.getStatsResult`.
- [ ] 2.6 Build and tests green.

## 3. Retrofit model/auth result events onto ResultEvent

- [ ] 3.1 Reparent `ModelListResultEvent` and `ModelModelsResultEvent` onto `ResultEvent` (add `id`); thread the originating `ModelListCommand`/`ModelModelsCommand` `id` through their emit sites in `src/Dmon.Core/Providers`.
- [ ] 3.2 Reparent `AuthStatusResultEvent` (and the auth completion result events) onto `ResultEvent` (add `id`); thread the originating command `id` through the auth handler emit sites.
- [ ] 3.3 Confirm each retrofitted event has a real originating command `id` at its emit site; if any emit site has no originating command, stop and raise it (per design risk note) rather than synthesising an id.
- [ ] 3.4 Update model-listing / auth tests to assert the correlation `id` on the result events.
- [ ] 3.5 Build and tests green.

## 4. Host consumption

- [ ] 4.1 Update `src/Dmon.Terminal` (and any shared RPC client) to consume the per-command typed result events and `commandError` instead of `{type:"response", data}`.
- [ ] 4.2 Where the host tracks pending requests, correlate completion by the result event's `id`.
- [ ] 4.3 Build and tests green; manual terminal smoke (`session.create`, `session.list`, `/model`, `auth.status`) behaves as before.

## 5. Finalisation

- [ ] 5.1 Grep the solution for residual `ResponseEvent` / `{type:"response"` references; confirm only the quarantined `session.getMessages` path remains.
- [ ] 5.2 `make build` clean (no warnings; `TreatWarningsAsErrors`).
- [ ] 5.3 `make test` (or `dotnet test -c Release`) green across all projects.
- [ ] 5.4 `openspec validate typed-command-result-events --strict` passes.
