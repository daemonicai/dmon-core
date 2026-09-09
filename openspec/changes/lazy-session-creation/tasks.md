## 1. Protocol: the `sessionStarted` event

- [x] 1.1 Confirm design D3's ADR reading before writing code: re-read ADR-015 and verify that a non-command event carrying `SessionMeta` extends the accepted model (as `ErrorEvent` does) rather than contradicting it. Verify by recording the conclusion in DEVLOG; if the reading does NOT hold, STOP and report — the path is a superseding ADR, not a workaround.
- [x] 1.2 Add `SessionStartedEvent` carrying `SessionMeta` to `core/Dmon.Protocol/Events/`, deriving from `Event` and NOT from `ResultEvent`, and register its `[JsonDerivedType]` discriminator `"sessionStarted"` on the `Event` base. Verify with a round-trip serialization test asserting the `type` discriminator is `"sessionStarted"` and that no command-correlation `id` is present.
- [x] 1.3 Regenerate the machine-readable wire-protocol schema export so it declares `sessionStarted`. Verify the `protocol-schema` freshness gate passes and fails if the event is removed from the export.

## 2. Core: create-and-activate seam

- [ ] 2.1 Extract the create-and-activate step out of `SessionHandler.CreateAsync` into one internal seam (create via `ISessionStore.CreateAsync`, set active, fire session-activity notification) that does NOT emit any event, leaving event emission to the caller. Verify existing `session.create` behaviour is unchanged: `session.createResult` is still emitted with the command id and the session is still active afterwards.
- [ ] 2.2 Route the existing `session.create` command path through the new seam. Verify the full existing SessionHandler test suite still passes with no changes to its expectations.

## 3. Core: lazy creation on first turn

- [ ] 3.1 In `TurnHandler`, when a turn is admitted and `ISessionHandler.CurrentSession` is null, create and activate a session via the §2 seam **before the turn executes** (design D2), binding it to the agent the core is already running (design D5). Verify with a test that submits a turn with no active session and asserts a session is active before the turn's first provider call.
- [ ] 3.2 Emit `sessionStarted` carrying the new `SessionMeta` when — and only when — the core creates a session on its own initiative. Verify with two tests: implicit creation emits `sessionStarted` and no `session.createResult`; explicit `session.create` emits `session.createResult` and no `sessionStarted`.
- [ ] 3.3 Turn the silent guard at `TurnHandler.PersistNewHistoryEntriesAsync` into a loud one: keep it as a defensive check but log a warning when it fires (design: "a silent guard is what caused this defect"). Verify with a test that asserts the warning is logged when the guard is reached.
- [ ] 3.4 Verify the end-to-end persistence fix: submit a turn with no active session and assert the completed turn's messages — including tool calls and tool results — are present in the new session's `messages.jsonl`. This is the defect this change exists to fix, so it must be covered by a test that fails against the pre-change code.
- [ ] 3.5 Verify a core that starts and never runs a turn creates no session directory (spec scenario "Core started but never asked to run a turn").
- [ ] 3.6 Verify the gateway path does not trigger implicit creation: after the two-step `session.create` → path-less `session.load` handshake, submitting a turn creates no second session and emits no `sessionStarted`.
- [ ] 3.7 Verify an implicitly created session is indistinguishable from an explicit one: `session.fork` and `session.load` against it succeed exactly as for a `session.create` session.

## 4. Console host: surface session start

- [ ] 4.1 Give `ConsoleEventHandler.TrackActiveSession` the display responsibility so every route that makes a session active (`created`, `forked`, `cloned`, `loaded`) produces a user-visible line identifying the session, not just internal tracking (design D6). Verify with tests asserting a scrollback line is produced for each of the four routes.
- [ ] 4.2 Handle the new `sessionStarted` event in `ConsoleEventHandler` through that same display path. Verify the displayed form matches the `/new` form, so explicit and implicit start are indistinguishable to the user.
- [ ] 4.3 Verify `console-host/spec.md`'s existing requirement is now met: `/new` displays the new session context (it previously tracked silently — this is the second defect the change fixes).

## 5. Regression safety for other hosts

- [ ] 5.1 Verify request/response tolerance: a host awaiting the result of an unrelated command receives `sessionStarted` mid-flight and the pending command still completes. Test at the `Dmon.Runtime` correlation layer rather than relying on inspection of `RpcTransportExtensions`.
- [ ] 5.2 Verify `Dmon.Desktop` is unaffected — it switches on known event types and must ignore `sessionStarted` without error.

## 6. Gates

- [ ] 6.1 Verify the whole solution builds warning-free: `make build` succeeds with `TreatWarningsAsErrors` on and no warnings suppressed or analyzers disabled.
- [ ] 6.2 Verify the full test suite passes: `env -u MEKO_API_KEY make test` reports zero failures.
- [ ] 6.3 Verify the change validates: `openspec validate lazy-session-creation --strict` passes.
- [ ] 6.4 Human-in-the-loop verification, to be confirmed by the Product Owner before this task is ticked. Run the TUI, type a question WITHOUT `/new`, quit, and confirm: (a) a session-context line appeared when the session started, and (b) the conversation is present in that session's `messages.jsonl`. Recipe: `bash demo/build.sh` then `export DMON_CORE_PATH="$PWD/build/demo/Agent.dll"` then `cd demo && dotnet run --project ../frontends/Dmon.Terminal`; the transcript lands under the repo's `.dmon/sessions/<id>/`.
