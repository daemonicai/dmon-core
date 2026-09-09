# DEVLOG — lazy-session-creation

Working record for the change. Organised by `## N.` section, mirroring `tasks.md`.
Append-only; only `## NEXT` is rewritten.

Branch: `fix/lazy-session-creation` (the change was proposed on this branch rather
than `change/lazy-session-creation`; the proposal commit `253060f` and the
`main`-greening build fix `2d62afd` both live here, so the branch is kept as-is).

## 1. Protocol: the `sessionStarted` event

**[architect]** Base: `253060f` — adds the non-command `sessionStarted` event to the protocol and to the machine-readable schema export.

**[architect]** Task 1.1 — ADR-015 gate: **the reading holds; no superseding ADR is required.**

Re-read `docs/adrs/ADR-015-typed-command-result-events.md` in full. Two clauses settle it:

- **§2 (A thin `ResultEvent` correlation base)** states explicitly: *"Streaming and notification events are **not** command results and remain id-less — `turnStart`, `messageStart`/`messageDelta`/`messageEnd`, `toolExecutionStart`/`End`, `sessionUpdated`, `compactionStart`/`End`, `retryAttempt`, etc. do not derive from `ResultEvent`."* The ADR therefore already partitions the event surface into command results (correlated, `ResultEvent`) and notifications (id-less, direct `Event` leaves) — and `sessionUpdated`, a session-domain notification, is named in that list.
- **§3 (Failures are correlated too)** retains `ErrorEvent` "for **non-command** core errors (it is not a `ResultEvent`)", confirming the non-command class is a live, intended part of the accepted model rather than legacy.

A `sessionStarted` event that derives directly from `Event`, carries `SessionMeta`, and carries **no** `id` is an instance of the class ADR-015 defines, not an exception to it. It also preserves the ADR's central goal — no opaque payloads, every leaf a flat `[JsonDerivedType]` describable by `JsonSchemaExporter` — because `SessionMeta` is already a named type carried by four existing result events.

Conversely, design D3's rejected alternative (reusing `SessionCreatedResultEvent`) *would* contradict §2: it would put a `ResultEvent` `id` on the wire correlating to no command.

Gate satisfied. Task 1.1 ticked on this record.

**[architect]** Block 1B (`1.2`–`1.3`) briefed: add the event leaf + regenerate the schema export. Scope fenced off `TurnHandler`/`SessionHandler`/`ConsoleEventHandler` — nothing emits the event after this block, by design.

**[worker]** Block 1B complete. `SessionStartedEvent : Event` (not `ResultEvent`) added to `OtherEvents.cs` next to `SessionUpdatedEvent`, carrying `SessionMeta` as `[JsonPropertyName("session")]`; discriminator registered on the `Event` base beside `sessionUpdated`. Wire shape `{"type":"sessionStarted","session":{...}}` with **no `id` key emitted at all**. `docs/protocol/schema.json` regenerated via `make schema`. New `test/Dmon.Protocol.Tests/SessionStartedEventSerializationTests.cs`.

**[reviewer]** Block 1B: **Approve** — no blockers, no nits.

- Confirmed `SessionStartedEvent` does not derive from `ResultEvent` and declares no `id`/`CommandId`.
- Confirmed the round-trip test serialises **as the `Event` base type**, so the polymorphic discriminator write path is genuinely exercised, and asserts absence of `id` structurally via `TryGetProperty` rather than a null check.
- Confirmed `docs/protocol/schema.json` is faithfully generator-produced by re-running `make schema` and getting a byte-identical file. The ~119-line diff for one added leaf is purely mechanical: inserting the leaf at `anyOf` index 19 moves the first *inline* definition of the shared `tokens`/`cost` subschema from index 32 to 19 and renumbers the downstream `#/anyOf/N/...` pointers. **No existing leaf was dropped or altered.**
- Confirmed the freshness gate genuinely reddens: deleting the `[JsonDerivedType]` line failed 4 tests, including the pre-existing `CommittedSchema_MatchesLiveExport` and `AllEventLeaves_AppearInSchema_KeyedByTypeDiscriminator`. Neither pre-existing gate was weakened or special-cased.

Gates: `make build` clean (no warnings), `env -u MEKO_API_KEY make test` green (`Dmon.Protocol.Tests` 105/105), `openspec validate lazy-session-creation --strict` passes.

**[architect]** Note for §3: the `make build` / `make test` gates must be run **unsandboxed**. Sandboxed runs hit the known `dotnet` runfile `build-start.cache` permission artifact — an environment signature, not a code failure.


**[supervisor]** Section 1 (`253060f..84530c7`): **Approve** — no blockers.

Verified beyond the block review, structurally rather than by reading the diff:

- **Schema integrity.** Parsed the old and new `docs/protocol/schema.json` and compared every discriminated leaf: **81 → 82 leaves, `only in old: []`, `only in new: ["sessionStarted"]`.** Nothing dropped or renamed. The 7 leaves whose bodies changed are pointer renumbering, semantically identical after ref-normalisation. `x-protocolVersion` unchanged (`0.2`), `$defs` 11 → 11, top-level `oneOf` 11 → 11. `make schema` regenerates byte-identical.
- **ADR-015 gate.** Agrees with task 1.1's conclusion on its own independent reading. ADR-015 §2 does not merely *tolerate* id-less events, it **partitions** the surface and names `sessionUpdated` — a session-domain notification — in the id-less list. No superseding ADR needed. The standing spec's event enumeration (`openspec/specs/agent-core/spec.md:229`) is open-ended, so section 1 owes no standing-spec edit either.
- **No dead scaffolding.** One type, one registration, one test file. The test asserting the *live* `ProtocolSchemaExporter.ExportAsJson()` (not the committed file) contains the discriminator makes 1.3's "fails if the event is removed" true independently of whether `schema.json` was regenerated.
- **Shape is what §3–§5 will want.** `SessionMeta` carries `Id`/`Name`/`Created`/`Modified` — enough for §4's console display without widening the DTO or adding a second event.

Architectural notes:

1. **`docs/protocol/README.md` §5.3 becomes wrong once §3 lands**, and no task in this change covers it. That file declares itself the source a non-.NET client is generated from, and §5.3 tabulates *the* set of notification events a `turn.submit` produces. After §3, a `turn.submit` against a session-less core emits `sessionStarted` **first**. This matters more than usual because the consumer that guide exists for — `dmon-home` — now lives in a separate repository and cannot be reached by §5's in-tree regression tasks; the README is the only channel that tells it.
2. **The session-notification pair is now asymmetric**: `sessionUpdated` carries `{id, title}`, `sessionStarted` carries `{session: SessionMeta}`. Not a defect (D3 consciously rejected reusing `SessionUpdatedEvent`, and the richer payload is right), but if a third session notification ever appears, converge on `SessionMeta`.
3. **Schema pointer stability.** `SessionMeta`'s canonical inline definition has **moved** from the `session.createResult` leaf to the new `sessionStarted` leaf, the other four `session.*Result` leaves now `$ref`ing into it — a property of the exporter (first occurrence inlines, later ones ref). Any future event registered earlier in `Event.cs` re-anchors it again and produces another wide-looking diff. Don't read that shape as damage next time.

**[architect]** Accepting supervisor note 1 as **in scope for this change** rather than a follow-up: the change is what makes the doc wrong, and shipping a wire-contract guide that under-describes the turn stream would leave the change in a partial state. It is a doc-only realignment, so it is the Architect's edit, not a worker's. It lands with section 3, when the event first has an emitter — until then the README is still accurate.

**[architect]** Recording the version decision the supervisor asked for, so a future change does not relitigate it: **`x-protocolVersion` stays `0.2`.** Under ADR-011 D5 compatibility is an *identical* `Major.Minor` and a host resolves `[0.2.0, 0.3.0)`; bumping to `0.3` for a purely additive event would force every host into lockstep for no wire break. ADR-015's own event-surface change *did* bump `0.1`→`0.2`, but that change was **breaking** (it deleted the `response` envelope). The rule is "breaking bumps the minor, additive does not" — not "event-surface changes bump the minor".

Section 1 closed.

## 2. Core: create-and-activate seam

**[architect]** Base: `84530c7` — extracts the create-and-activate step behind one seam both the explicit and implicit paths use, so they cannot diverge.

**[architect]** Block 2A (`2.1`–`2.2`) briefed. Two calls made in the brief, both load-bearing:

- **The seam goes on the `ISessionHandler` interface, not private to `SessionHandler`.** `TurnHandler` holds an `ISessionHandler` (`core/Dmon.Core/Rpc/TurnHandler.cs:31`), so a private method would be unreachable from section 3 and is the wrong cut.
- **The eight test fakes implementing `ISessionHandler` are part of this block**, not a later one — adding an interface member reddens all of them at once.

**Design D5 resolved by the architect.** D5 binds the implicit session to "the agent the core is already running". Tracing it: the core process carries **no agent name**. `agent` is a per-command `string?` the host supplies on `SessionCreateCommand`; `Dmon.Terminal` supplies nothing (so `/new` creates with `agent: null`), and the gateway supplies `createFrame.Agent` — but the gateway's two-step create → path-less load handshake means the lazy path never fires there. So the implicit path passes **`null`**, which is D5's own second clause ("the same value an explicit `session.create` would resolve to by default") and satisfies D5's stated purpose exactly: an implicitly created session's `meta.json` is byte-identical to an explicitly created one from the same host. Inventing any other value (e.g. `"default"`) would be precisely the divergence D5 exists to prevent. The seam still takes `string? agent` so a future in-core agent identity has somewhere to go.

**[worker]** Block 2A complete.

- Added to `ISessionHandler`, implemented in `SessionHandler`: `Task<SessionMeta> CreateAndActivateAsync(string? agent, CancellationToken cancellationToken)`. It creates via `_store.CreateAsync(name: null, agent, …)`, sets `_currentSession`, calls the existing `NotifySessionActivated`, returns the meta — and **emits nothing**.
- `SessionHandler.CreateAsync` now calls the seam and then emits `SessionCreatedResultEvent { CommandId = cmd.Id, Session = meta }`. Order preserved: create → activate → notify → emit.
- Six fakes that do not exercise session creation got a `throw new NotSupportedException()` stub. The two in `TurnHandlerIntegrationTests.cs` (`StubSessionHandler`, `ActiveSessionHandler`) — the ones section 3 will drive — got real minimal implementations instead: `CurrentSession` became `{ get; private set; }`, and the seam mints a fresh `SessionMeta`, activates it, and returns it.

Gates: `make build` 0 warnings, `env -u MEKO_API_KEY make test` all green (`Dmon.Core.Tests` 613 passed / 1 skipped / 0 failed), `openspec validate lazy-session-creation --strict` valid.

**[reviewer]** Block 2A: **Request changes** → resolved. One blocker, and it was the **Architect's**, not the worker's: `DEVLOG.md` had been truncated to 15 lines, destroying the whole section-1 record. Cause: the Architect's edit script located the `## NEXT` heading with a plain string search, which matched the phrase "only `## NEXT` is rewritten" in the file's own header on line 4 and cut everything after it. Restored from `84530c7` and re-appended; the edit now anchors on a line-start `^## NEXT$` regex and asserts exactly one match. **The block's production diff needed no rework.**

Everything else the reviewer verified clean:

- Seam creates → activates → notifies → returns, emits nothing; `NotifySessionActivated`'s swallow-and-log tolerance preserved.
- `session.create` routes through it with a single call site — no double emission, no double notification.
- D5 honoured: `agent` passed straight through, no defaulting, no config read, no agent-name concept added to core.
- **Double-notify is genuinely caught, not merely nitted:** `test/Dmon.Core.Tests/Rpc/SessionActivityListenerTests.cs:56-66` asserts `Assert.Single(listener.ActivatedIds)` on `CreateAsync`, and that file is unmodified by the diff.
- **No existing test expectation changed** (task 2.2's explicit requirement): the two files that actually exercise `SessionHandler` behaviour — `SessionActivityListenerTests.cs` and `SessionHandlerTypedEventsTests.cs` — have **zero** diff. All eight touched test files only *add* an interface member.
- The six throwing stubs are honest: nothing outside `SessionHandler.CreateAsync` calls the seam yet, so no test path can reach a `NotSupportedException`.
- `CurrentSession` becoming `{ get; private set; }` is inert for every existing test — both fakes are only ever constructed via their constructor.
- Scope clean: `TurnHandler.cs`, `ConsoleEventHandler.cs`, `Dmon.Runtime`, `Dmon.Desktop`, and `ForkAsync`/`CloneAsync`/`LoadAsync` untouched, confirmed by diffstat.

Nit carried to section 3: the two `TurnHandlerIntegrationTests.cs` fakes' `CreateAndActivateAsync` invokes no `ISessionActivityListener` and neither fake takes one — fine for what section 3 needs, but a divergence from the real seam that a fake-only test would not catch.

**[supervisor]** Section 2 (`84530c7..24b819b`): **Approve** — no blockers.

Gates re-run unsandboxed and green: `make build` 0 warnings / 0 errors; `env -u MEKO_API_KEY make test` all suites (`Dmon.Core.Tests` 613 passed / 1 skipped / 0 failed, Terminal 187, Network 223, Memory 51, Routing 45, Dcal 14, Providers 32, Mtplx 32); `openspec validate --strict` valid. Section diff +97/−9 across 10 files.

**On the question the section exists to answer — does the seam make divergence *impossible*, or only unlikely?** Impossible, at the `SessionHandler` boundary. The supervisor enumerated everything `CreateAsync` did before the extraction against everything now left outside the seam: the seam is `_store.CreateAsync` → `_currentSession = meta` → `NotifySessionActivated` → return, and `CreateAsync` retains **only** the `SessionCreatedResultEvent` emission. No residual create-or-activate state is left for a caller to set, so section 3 cannot legitimately produce a differently-shaped session. It also checked the two places divergence could hide *above* the seam and cleared both: `SessionLock` is acquired by `LoadAsync` only (neither create path locks — symmetric and pre-existing), and `CommandDispatcher` routes `SessionCreateCommand` straight to `CreateAsync` with no post-step, unlike `LoadAndSeedAsync`. **D4's structural claim holds.**

**Seam shape justified, not over-built.** `Task<SessionMeta> CreateAndActivateAsync(string? agent, CancellationToken)` is the minimum that works: §3 needs the returned meta for `sessionStarted`, and `agent` is required because the *explicit* path must pass `cmd.Agent` through the same single `_store.CreateAsync` call — which is exactly what makes divergence impossible. Removing the parameter would break D4, not simplify it. D5's `null` resolution agreed.

Two findings, both discharged as section-3 obligations rather than a remediation block (see `## NEXT`): `3.2` must assert event **absence** structurally, and `3.4`–`3.7` must run against the real `SessionHandler` + `SessionStore`, not the integration-test fakes.

Architectural notes:

1. **`ISessionHandler` shape drift.** Every other member is `Task X(SomeCommand cmd, ct)` — a command handler. `CreateAndActivateAsync` is a *capability*, and is now the only non-command member besides the `CurrentSession` property. The Architect's call is defensible and not worth re-opening for one member, but **if a second non-command capability ever lands, split them** — a narrow `ISessionActivator` that `SessionHandler` also implements keeps the command contract command-shaped and stops the eight test fakes growing a throwing stub each time.
2. **Public API addition on a packable first-party assembly (ADR-024).** `ISessionHandler` is public surface of `Dmon.Core`; adding an interface member is source-breaking for any external implementor. There are none and there are no production deployments, so it is a clean break — but it is a `Dmon.Core` package-version consequence nobody has declared. Worth one line in §6 if the release matrix is touched. No protocol `Major.Minor` implication; section 2 leaves the wire untouched.
3. **The six `NotSupportedException` stubs are honest and unreachable today**, but become a latent runtime hazard the moment any *other* core component calls the seam through an injected `ISessionHandler`. If a later change adds such a caller, those stubs need real implementations rather than a widened blast radius.

Section 2 closed.

## 3. Core: lazy creation on first turn

**[architect]** Base: `24b819b` — the defect fix itself: a turn submitted with no active session creates one before it executes, announces it, and is persisted.

**[architect]** Block 3A (`3.1`–`3.3`) briefed with the exact insertion point and both supervisor constraints (absence-assertions; fakes are adequate for 3.1–3.3 only).

**[worker]** Block 3A complete.

- **Insertion** at `TurnHandler.SubmitAsync` (`core/Dmon.Core/Rpc/TurnHandler.cs:128-134`), inside the existing `try`, after `_turnCts` is created and **before** `NotifyTurnStarted` — so the asset provisioner and system-prompt build see a non-null session id (design D2). When `CurrentSession is null`: `CreateAndActivateAsync(agent: null, _turnCts.Token)` (design D5), then emit `SessionStartedEvent { Session = createdSession }`. The emission lives in `TurnHandler`, not the seam.
- **Token:** `_turnCts.Token`, consistent with every other await in the method. The new code sits inside the `try` whose `finally` unconditionally disposes `_turnCts` and releases `_turnGate`, so a throwing or cancelled creation cannot leak the gate.
- **Guard (3.3)** split into two clauses: `_sessionStore is null` returns **silently** (a core running without persistence is a legitimate configuration, and warning every turn would be noise); `CurrentSession is null` now warns. Structured-logging style with the count of what was lost: `"…skipping persistence of {DiscardedEntryCount} history entries for this turn."`
- **Rewrote an existing test that encoded the defect.** `Submit_WithNoActiveSession_DoesNotCallSessionStore` asserted `AppendMessagesCallCount == 0` — the silent discard itself. Now `Submit_WithNoActiveSession_CreatesSessionLazilyAndPersists`, asserting the session is created and the persisted session id matches it.
- New test doubles: `SessionAtCallCapturingChatClient` (captures `CurrentSession?.Id` at the first provider call), `BrokenActivationSessionHandler`, `CapturingLogger<T>` (no logger-capture fake existed in this project).

Gates: `make build` 0 warnings, `env -u MEKO_API_KEY make test` green (`Dmon.Core.Tests` 620/620, 1 pre-existing unrelated skip), `openspec validate --strict` valid.

**[reviewer]** Block 3A: **Approve** (one nit, fixed before commit).

The two items most able to be green-but-worthless were both checked and both hold:

- **The ordering proof is real.** `SessionAtCallCapturingChatClient` reads `CurrentSession?.Id` at the moment `GetResponseAsync`/`GetStreamingResponseAsync` is first invoked, i.e. inside `RunTurnAsync`. Moving creation to persist time would make the captured id null and fail the test. Not a post-hoc check.
- **The rewritten test is a real regression guard, not a tautology.** Confirmed against `git show 24b819b:…` that the old test asserted `AppendMessagesCallCount == 0` — literally the defect — and that the replacement asserts the persisted `SessionId` equals the newly created session's id.

Also verified: both absence-assertions exist and are on the correct seams (`Assert.Empty(…OfType<SessionCreatedResultEvent>())` on the implicit path; new `CreateAsync_ExplicitCreation_DoesNotEmitSessionStarted` with `Assert.Empty(…OfType<SessionStartedEvent>())` on the explicit path); `_turnGate` release is safe on a throwing/cancelled creation (the new code is inside the existing `try`/`finally` extents, traced rather than trusted); the guard split is sound; `Submit_GuardReached_LogsWarning` asserts `LogLevel.Warning`, not merely that something was logged; emit-once is covered by a second-turn test and an already-active test; `TurnHandlerFactory.Create`'s new `ILogger` parameter is the *test* helper, not production DI, so no production call site changed.

**Verdict on `BrokenActivationSessionHandler`:** honest, but it manufactures a state the real seam cannot produce (`CurrentSession` hard-coded to `null`). Acceptable as defence-in-depth coverage of deliberately defensive code — **not** a live-path proof. Carried to 3B.

Nit (fixed): the warning was a plain concatenated string rather than the file's structured-logging style, and did not say how much was lost — weak for a guard whose entire purpose is to make a future failure visible. Now carries `{DiscardedEntryCount}`. The test asserts a positive count by regex rather than a hardcoded literal, since the exact count depends on pipeline internals.

**[architect]** Block 3B (`3.4`, `3.5`, `3.7`) briefed **tests-only**, with the supervisor's real-stack ruling as a hard constraint and an explicit instruction to *report, not adjust*, if any test needed production changes to pass. Also asked for the confirmation the 3A reviewer wanted: that the real `CreateAndActivateAsync` cannot return a `SessionMeta` without setting `CurrentSession`.

**[worker]** Block 3B complete — one new file, `test/Dmon.Core.Tests/Rpc/LazySessionCreationRealStackTests.cs`, no production change.

- Wired against a **real** `SessionHandler` over a **real** `SessionStore` and a real `AttachmentStore`, isolated by a private `FakeResolver` that redirects only `ISessionDirectoryResolver.Resolve()` to a temp path.
- **3.4** drives a real tool call through `FunctionInvokingChatClient` (reusing the existing `FunctionCallProviderStub` + `StubToolRegistry`), then asserts **from disk**: the session dir and `messages.jsonl` exist, and `ReadRecordsAsync` yields an `assistant` record carrying a `ToolCallPart` named `stub_tool` **and** a `tool`-role record carrying a `ToolResultPart`.
- **3.5** constructs the handlers as production wiring does, submits no turn, and asserts the sessions root does not exist.
- **3.7** builds two independent real stacks (implicit vs explicit), forks and loads both, and compares each fork against **its own** source session.
- Added `CreateAndActivateAsync_AlwaysSetsCurrentSessionBeforeReturning` — requested in the brief, so accounted for, not unscoped extra.

**Pre-change proof for 3.4** (task requires the test fail against pre-change code): copied `TurnHandler.cs` from `24b819b` over the working file, ran the test, saw it fail at `Assert.NotNull(created)`, restored. The Architect verified afterwards that `TurnHandler.cs` is byte-identical to `HEAD`.

**[reviewer]** Block 3B: **Approve** (two cosmetic nits, both fixed before commit).

The binding question for this block was whether the real-stack ruling was actually honoured. **It was — `FakeResolver` is a compliant seam, not a violation.** `ISessionDirectoryResolver.Resolve(string)` is pure path computation (it walks up looking for `.dmon/config.yaml` and returns a root string); it never touches `SessionMeta` and is not part of `ISessionStore`. `SessionStore.GetRoot()` calls the resolver and then does the real `Directory.CreateDirectory` **itself**, and every store method underneath is genuine I/O — `Directory.CreateDirectory`, `File.Create`, `FileStream` read/write, JSON (de)serialisation, index upserts. Decisively: the `AttachmentStore` is wired through the *same* `FakeResolver` instance, so the creating and appending paths share one resolver — exactly the property the prohibited fakes lack.

- **3.4 reads real bytes.** `ReadRecordsAsync` goes through the real `FileStream`/`StreamReader`/`JsonSerializer` path, not an in-memory round trip. Both sides of the tool round trip are asserted as separate `MessageRecord`s.
- **The pre-change proof is precise, not a conflation.** Diffing `24b819b`'s `SubmitAsync` against HEAD: the old code never called `CreateAndActivateAsync` at all, and its guard was `if (_sessionStore is null || CurrentSession is null) return;` — so a session-less turn ran to completion and then silently skipped persistence entirely. Failing at `Assert.NotNull(created)` lands on exactly that root cause; "no session created" and "nothing persisted" are the same defect here.
- **3.5 is a real regression test.** `SessionStore.GetRoot()` — the only thing that creates the root — is reachable only from inside `ISessionStore` methods, and both handler constructors do pure field assignment with no eager store call. A regression to eager/startup creation would make the root exist and turn this red.
- **3.7 establishes parity, not dual success.** Each fork is compared against **its own** source (`ParentSession`/`ForkEntryId`/`Agent`), which is the correct shape — cross-comparing two independent sessions would be wrong. These are the fields that would diverge if the implicit path forked differently.
- **Hygiene verified empirically**: `TempSessionsRoot : IDisposable` used via `using`, so cleanup survives a failing assert; `.dmon/sessions` was **770 entries before and after** the full run, and `git status` showed no tracked-file changes. Nothing added to the litter design D1 exists to avoid.
- Confirmed no 3A test was weakened, renamed or deleted, and no 3.6/gateway work leaked in.

Nits, both fixed: the raw-text `Contains("\"toolCall\"")`/`"toolResult"` checks were a whole-file substring scan, redundant with the structured assertions and liable to give a future reader false confidence — **dropped** (the structured `ToolCallPart`/`ToolResultPart` assertions are the real evidence); and a leftover `await Task.CompletedTask;` — removed, with `CoreStartedButNeverSubmitsATurn_CreatesNoSessionDirectory` made a plain `void` `[Fact]` since it awaits nothing.

Gates after the nit fixes: `make build` 0 warnings, `env -u MEKO_API_KEY make test` full suite green (`Dmon.Core.Tests` 624 passed / 1 skipped / 0 failed), `openspec validate --strict` valid.

**[architect]** Noted from the worker's report: a standalone `dotnet test --filter` invocation hit a `vstest.console`/testhost connection flake. Not treated as evidence of anything — the full-suite run is the gate and it is green, and the same filtered test passed cleanly before the edits. Recording it only so a future session recognises the signature rather than re-diagnosing it.

**[reviewer, architectural note]** The `FakeResolver` pattern here — real store, real filesystem, isolated only at the directory-resolution seam — is a better template than the `SpySessionStore` fakes that `TurnHandlerIntegrationTests` still leans on. Worth considering whether those fake-store tests should eventually be supplemented by this pattern, for the same reason that drove this block's ruling. **Out of scope for this change** — parked here rather than actioned.

**[architect]** Block 3C (`3.6`) briefed tests-only, with two named options — (1) drive the real handshake through the `Dmon.Network` harness, (2) re-enact its effect at core level — and an instruction to prefer (1) unless it needed heroics, and to state explicitly what (2) would not prove if it fell back.

**[worker]** Block 3C complete — fell back to **option (2)**, core level, with the reasoning stated up front rather than discovered in review: `Dmon.Network.Tests`' gateway harness backs `DriveSessionHandshakeAsync` with a `FakeCoreProcess` that only replays scripted stdout lines, so a turn driven through it would never reach `TurnHandler.SubmitAsync` — it would echo scripted lines and exercise nothing.

`GatewayHandshakeThenTurn_NoImplicitCreation_NoSecondSession` (added to `LazySessionCreationRealStackTests.cs`) re-enacts the handshake's effect against a real `SessionHandler` + `SessionStore` — explicit `SessionCreateCommand`, then a **path-less** `SessionLoadCommand` — then submits a turn and asserts `Assert.Empty(…OfType<SessionStartedEvent>())`, an unchanged on-disk session-directory count (**1 both before and after**), and an unchanged active session id. `.dmon/sessions` 770 before and after.

**[reviewer]** Block 3C: **Approve** — no nits.

**The worker's justification for dropping to core level was verified on its merits, not accepted.** The reviewer read `test/Dmon.Network.Tests/NetworkCreateE2ETests.cs` in full: `FakeCoreProcess` is a bare `ICoreProcess` over a caller-supplied `TextReader`/`TextWriter` pair with no `SessionHandler`, no `TurnHandler` and no session store — the tests script `session.createResult`/`session.loadResult` lines by hand. There is no path by which a `turn.submit` through that harness could reach the lazy branch. A grep of the whole test tree found the only real `ICoreLauncher` in any test project spawns an actual OS process (`test/Dmon.Core.Tests/Integration/LiveToolCallE2ETest.cs`) — so option (1) would have meant a live-process integration test. **The fallback is correct engineering, and the up-front scoping a credit.**

**What remains unproven by test and rests on source inspection:** that the real `DriveSessionHandshakeAsync` completes create-then-load in that order before returning, leaving a session active on the wire. The reviewer confirmed the worker's characterisation is accurate **and complete** — that is the only gap, and it is itself partly covered by existing wire-level tests (`NetworkCreateE2ETests.HandleCreate_HappyPath_…` asserts create-before-load ordering into stdin; `NetworkCreateFlowTests` 6.1a/6.1b assert the returned session id and post-handshake state). It found no divergence between the re-enactment and the real handshake in ordering, path-less-load semantics, or what the gateway consumes before its pump starts.

Also verified:

- **The directory-count assertion is sound and the strongest claim in the block.** `GetSessionDirectory(id)` is `Path.Combine(GetRoot(), id)`, and nothing else creates a directory at root level (attachments live under `sessionDir/attachments`; `SessionIndex` writes a *file*, `index.db`). Asserting `== 1` both times is strictly stronger than a before/after comparison, and both properties are present.
- **The path-less load branch is genuinely driven** — `SessionHandler.LoadAsync`'s `cmd.Path is null` path falls back to `_currentSession.Id`, which is exactly what the gateway does and the reason `CurrentSession` is non-null at turn time.
- **The test can fail**: an unconditional-creation regression trips *both* assertions; a regression that treated the handshake session as "not real" would still trip the `SessionStartedEvent` one.
- Hygiene, scope (additive-only, 82 lines, no production file touched, no section-5 work, no existing test modified) and style all clean.

Gates: `make build` 0 warnings, `env -u MEKO_API_KEY make test` green (`Dmon.Core.Tests` 625 passed / 1 pre-existing skip), `openspec validate --strict` valid.

**[supervisor]** Section 3 (`24b819b..1ce9690`): **Request changes** — one blocker. *(Remediated below; see the round-2 verdict.)*

What it verified and cleared first — the two structural claims the section rests on both hold, independently confirmed rather than taken from the DEVLOG:

- **`ISessionStore.CreateAsync` has exactly one caller** (`SessionHandler.CreateAndActivateAsync`), whose only two callers are explicit `session.create` and the new lazy branch. **Design D1 is therefore structurally true, not merely test-asserted** — there is no path that creates a session before a turn needs one.
- **The silent `_sessionStore is null` guard clause is not a production hole.** `ISessionStore` and `TurnHandler` are registered in the same `AddDmonCore` (`core/Dmon.Core/DaemonServiceExtensions.cs:95`, `:130`) and `SubmitAsync` is the only entry point that runs a turn, so a store-less core is test-only.
- Cancellation, a throwing `CreateAndActivateAsync` (dispatcher emits `internalError`; the `finally` releases `_turnGate`), the follow-up/steer loop (persist runs after `break`, on `CancellationToken.None`) and compaction all check out.
- **D5 holds on the implemented result, not just the reasoning**: `/new` sends `SessionCreateCommand { Id }` with `Agent` unset (`frontends/Dmon.Terminal/SlashCommandParser.cs:59-63`), so `agent: null` is byte-for-byte what `/new` produces, and both paths hard-code `name: null` in the same seam.
- The two test bodies (fakes in `TurnHandlerIntegrationTests`, real stack in `LazySessionCreationRealStackTests`) are a deliberate, recorded fidelity pyramid, not cross-block drift.
- `BrokenActivationSessionHandler` still earns its place: task 3.3 demands a test that reaches the guard and the real seam by construction cannot, while 3B's `CreateAndActivateAsync_AlwaysSetsCurrentSessionBeforeReturning` documents that relationship rather than contradicting it.

**BLOCKER — `sessionStarted` could be silently dropped, permanently.** `TurnHandler.cs:129-135` emitted the event on `_turnCts.Token`, and `EventEmitter.EmitAsync` honours its token (`core/Dmon.Core/Rpc/EventEmitter.cs:21`). So a `turn.abort` (Esc in the TUI) or a shutdown landing between the create returning and the emit running would throw `OperationCanceledException`, which `CommandDispatcher.RunGuardedAsync` swallows by design (`CommandDispatcher.cs:84-87`) — **while the session is already created, on disk, and active**. The host never learns it exists, and never recovers: every later turn finds `CurrentSession` non-null, so the event is never re-emitted, and for the rest of that core's life the host writes to a session id it was never told about. That is exactly the core/host divergence this change exists to eliminate, and leaves unmet the spec clause *"so a host learns the identity of a session it never requested"*.

The supervisor noted this was a **slipped block-level finding**: the 3A reviewer examined this very token for `_turnGate`-leak safety and cleared it on those grounds, but the *delivery* consequence went unexamined. The file already had the convention two callers away — `TurnEndEvent` and the provider-switch emits use `CancellationToken.None` with the comment *"these emits must reach the host even when the prior turn aborted"*.

**[architect]** Carved a remediation block from the finding. Ticks nothing — every section-3 box is already ticked — and lands as a `fix:` commit, per the apply workflow. Folded in the supervisor's architectural note 1 (a comment-only correction) since it touches the same file.

**[worker]** Remediation complete.

- `TurnHandler.cs:136` emits `SessionStartedEvent` on **`CancellationToken.None`**, with a comment matching the house style of the two sibling sites but carrying the session-specific rationale: the session is already durable on disk, so the emit must reach the host regardless of turn cancellation or the host permanently loses track of its identity. `CreateAndActivateAsync` is untouched, still on `_turnCts.Token` — a cancelled *creation* correctly means there is nothing to announce.
- New test `Submit_TurnCancelledBeforeSessionStartedEmit_StillEmitsSessionStarted` plus a `CancelsOnCreateSessionHandler` double that cancels the outer CTS **synchronously inside** `CreateAndActivateAsync` before returning.
- Comment-only correction to `CoreStartedButNeverSubmitsATurn_CreatesNoSessionDirectory`: it no longer claims to construct the handlers "exactly as the core start path does", since the real path also runs `BootstrapService.RunAsync`, which creates the sessions root on first run. No assertion changed.
- **Red/green proof:** stashed only the `TurnHandler.cs` fix, ran the new test alone → `Assert.Single() Failure: The collection was empty`, i.e. the event genuinely never reached the emitter. Restored → passes.

**[reviewer]** Remediation block: **Approve** — no blockers, no nits.

- **The hole is closed.** Traced `EmitAsync`: the only two interruptible points are `_gate.WaitAsync` and the `WriteLineAsync`/`FlushAsync` pair, both now on `None`; `JsonSerializer.Serialize` on a plain DTO is neither cancellable nor blocking. `RunGuardedAsync` swallows only `OperationCanceledException`, and the emit can no longer produce one.
- **No new wedge shape.** After acquiring `_gate`, `EmitAsync` does exactly one `WriteLineAsync` + `FlushAsync` and releases — structurally identical to the seven existing `CancellationToken.None` emit sites in the same file. A stuck stdout write was already a systemic risk; this does not worsen it or make it reachable in a new state. The worker's "same class" argument was assessed on its merits and holds.
- **The test is deterministic, not racy.** The double cancels before returning a `Task.FromResult`, so the `await` in `SubmitAsync` resumes synchronously with no thread hop — `_turnCts.Token` (linked from the outer token) is guaranteed cancelled by the time the emit line runs. `TestEventEmitter` mirrors the real emitter's token-honouring `_gate.WaitAsync`, so reverting the fix reproduces the real failure mode rather than an artificial one.
- **The comment fix is accurate**: `BootstrapService.RunAsync` does `Directory.CreateDirectory(sessionsPath)` unconditionally on first run (`core/Dmon.Core/Bootstrap/BootstrapService.cs:63-65`), so the test's "no root" result really is stronger than real-core behaviour.
- Scope clean: three files, `tasks.md` diff against `1ce9690` empty, additive-only, no existing test weakened.

Gates: `make build` 0 warnings, `env -u MEKO_API_KEY make test` green (`Dmon.Core.Tests` 626 passed / 1 pre-existing skip), `openspec validate --strict` valid.

**[supervisor]** Section 3, **round 2** (`24b819b..ed850e6`): **Approve**.

- **The blocker is genuinely closed, not relocated.** Traced `EmitAsync` independently: its only two token-observing points are `_gate.WaitAsync` and the `WriteLineAsync`/`FlushAsync` pair, both now on `None`; serialising a plain DTO neither blocks nor cancels. The emit can no longer produce the `OperationCanceledException` the dispatcher swallows.
- **No unreached sibling of the same bug.** The section adds exactly one path that mutates `CurrentSession`; `CreateAndActivateAsync` sets `_currentSession` then notifies with no post-store token check, so it cannot return created-but-unannounced, and its only two callers are explicit `session.create` and the lazy branch.
- **The wedge question was judged independently, not taken from the block review.** `DrainAsync` does `Task.WhenAll` over the turn tasks, so an uncancellable emit *can* hold shutdown — but that exposure already exists in the same task through the same emitter (`PersistNewHistoryEntriesAsync`, the `TurnEndEvent` emit, the provider-switch emit are all `None`). The new site adds one more line-write under the same semaphore, not a new failure mode. Firing earlier costs only that a shutdown-time abort writes one line before the turn body throws. Bounded, and the right trade for an event whose loss is permanent.
- **The remediation test is honest.** The double manufactures a state that is genuinely reachable in production — `_turnCts` is linked over the caller's token, `AbortAsync` cancels it from a concurrent dispatch, and `CreateAndActivateAsync` has no post-store token check, so "created, activated, returned, token already cancelled" is real. The double is a scheduling device making it deterministic rather than racy. `TestEventEmitter` mirrors the real emitter's token-honouring point, so a regression reproduces the *real* failure.
- **Spec satisfied as a whole**: both requirements and all nine scenarios have a test that actually asserts them, and the schema clause is met (`docs/protocol/schema.json:909` declares `"const": "sessionStarted"`). **The proposal's root cause is closed** by the real-filesystem test asserting the tool call *and* its result reach `messages.jsonl`. Emission ordering also holds — the emit precedes `NotifyTurnStarted` and `TurnStartEvent`, matching the paragraph added to `docs/protocol/README.md`.
- Nothing disturbed: three files, additive only, the test comment change verified comment-only by diff, `tasks.md` unchanged against `1ce9690` (correctly — every section-3 box was already ticked). No cross-block drift, no dead scaffolding, no ADR erosion.

Gates: `make build` 0 warnings, `env -u MEKO_API_KEY make test` green (`Dmon.Core.Tests` 626 passed / 1 pre-existing skip), `openspec validate --strict` valid.

**[architect]** Independently re-ran the gates rather than relying on the agents' reports: `make build` 0 warnings / 0 errors; `env -u MEKO_API_KEY make test` **all 20 suites green** — `Dmon.Core.Tests` 626/1 skip, `Dmon.Terminal.Tests` 187, `Dmon.Network.Tests` 223, `Dmon.Runtime.Tests` 44, `Dmon.Desktop.Tests` 54, `Dmon.Protocol.Tests` 105, remainder green. Section 3 closed.

Three architectural notes carried to `## NEXT` — a third `tech-debt/` file (half-created session directory), a standing decision to make consciously (`DrainAsync` has no timeout), and a nit (a fourth `ISessionHandler` double in one file).

## 4. Console host: surface session start

**[architect]** Base: `ed850e6` — the second defect this change fixes: `/new` currently tracks a new session **silently**, so the one command a user must know in order to avoid losing their conversation gives no sign it worked.

**[architect]** Block 4A (`4.1`–`4.3`, all of section 4) briefed as one block, with design D6's structural property called out as the thing the reviewer would check: the display must live **inside** `TrackActiveSession`, not be added per-case, so a future fifth route cannot be silent by omission. Left two judgement calls to the worker with an instruction to justify them: what the line actually says (both creation paths pass `name: null`, so in practice it carries a raw GUID), and whether the four routes should be textually distinguishable.

**[worker]** Block 4A complete. `TrackActiveSession` now takes a `verb` and calls `_renderer.AddSystemLine($"[Session] {verb}: {session.Id}")` **before** setting `ActiveSessionId`. The four existing call sites pass `"Started"`/`"Forked"`/`"Cloned"`/`"Loaded"`; a new `SessionStartedEvent` case passes `"Started"` — the same verb and the same format string as `/new`, so `4.2`'s identical-form requirement is met exactly. Full session id, not a truncated prefix: a short prefix risks ambiguity and the id is what session-targeting commands key off. Empty-id guard preserved and extended to cover the display (returns early, tracks nothing, prints nothing).

**[architect]** Confirming the worker's two judgement calls, both sound. Distinct verbs for created/forked/cloned/loaded are right — D6 mandates that the *display responsibility* be central, not that the text be identical across all five routes, and those four are semantically different operations the user issued deliberately. `4.2`'s sameness requirement applies only to `/new` vs `sessionStarted`, and using the same verb through the same format string satisfies it structurally rather than by coincidence.

**[reviewer]** Block 4A: **Approve** — no blockers.

- **D6's structural property holds.** `TrackActiveSession` is the **only** site in the file that assigns `ActiveSessionId` (verified, not assumed), and the single `AddSystemLine` inside it fires before that assignment on every route. A hypothetical sixth route calling it cannot be silent. No per-case duplication.
- **`4.2`'s identical-form test is real, not a format-string comparison.** It builds two separate handler/fake pairs, drives them through genuinely distinct case arms (`ConsoleEventHandler.cs:208` vs `:224`), and compares **actual rendered scrollback text**.
- **All four routes have their own test** — four independent methods, not one standing in for four.
- **Empty-id guard** returns before both the display and the tracking, with a test asserting neither happens. `SessionMeta.Id` is `required` and set from the store's generated id, so an empty id is unreachable in production — the reviewer agrees the early return is defensive rather than a re-creation of the silent-tracking bug.
- **Checked for a double-announcement** and found none: `BootstrapNoticeEvent` fires only on first-ever `.dmon` root creation and reports a directory path, not a session id — unrelated in subject to `sessionStarted`.
- Style clean: `[Session]` fits the file's bracketed-tag convention, the verbs read naturally, the doc comment cites the design decision rather than restating code.

Nit, pre-existing and out of scope: the untouched `SessionUpdatedEvent` case (`:177-179`) also prints under `[Session]` but in a different grammar. Carried to `## NEXT`.

**[architect]** Gates for this block run **by the Architect**, sequentially, after the diff was in place — not delegated: `make build` 0 warnings / 0 errors; `env -u MEKO_API_KEY make test` **all 20 suites green**, `Dmon.Terminal.Tests` **194 passed / 0 failed** (up from 187 — the seven new tests), `Dmon.Core.Tests` 626 / 1 pre-existing skip; `openspec validate --strict` valid.

**This was necessary, not belt-and-braces.** The worker reported `dotnet pack` "Operation not permitted" and `CreateAppHost` failures and classified them as pre-existing environment artifacts, declaring its gates passed. They were **not** pre-existing — see the concurrency warning in `## NEXT`. Had that report been taken at face value, section 4 would have been committed on unverified gates.

## NEXT

Section 4's block has landed; **`[supervisor]` review of `e200255..HEAD` pending**. Then section 5 (`5.1` runtime correlation tolerance, `5.2` Desktop), then gates `6.1`–`6.3`.

**`6.4` is human-in-the-loop and belongs to the Product Owner.** Do not tick it on any agent's say-so. Recipe from `tasks.md`: `bash demo/build.sh`, then `export DMON_CORE_PATH="$PWD/build/demo/Agent.dll"`, then `cd demo && dotnet run --project ../frontends/Dmon.Terminal`; type a question **without** `/new`, quit, and confirm (a) a session-context line appeared and (b) the conversation is in that session's `messages.jsonl` under the repo's `.dmon/sessions/<id>/`. Section 4 means the line to look for is `[Session] Started: <guid>`.

**⚠ NEVER RUN GATES CONCURRENTLY WITH AN AGENT.** Parallel `dotnet` builds race on shared `$TMPDIR` pack output paths and produce permission-shaped failures that are neither permission problems nor code failures: `Pack.targets(226,5): Access to the path '$TMPDIR/dmon-feed-<guid>/<Pkg>.nupkg' is denied. Operation not permitted` (kills ~16 `Dmon.Core.Tests` pack-based tests), `HostWriter.CreateAppHost` MSB4018 (kills `make build` on `Dmon.Terminal.csproj`), and `InitCommandTests` failures. **This bit us in block 4A**: the worker hit it, reported it as a "pre-existing environment artifact", and declared its gates passed — a **false green**, when a fully green run had happened twenty minutes earlier. Diagnosis order: the named `dmon-feed-<guid>` dir usually does **not exist** (a create failure, not stale state); `$TMPDIR` probes writable; `ps -eo pid,etime,comm | grep dotnet` shows a cluster of same-age processes. Re-run **sequentially** (`MSBUILDDISABLENODEREUSE=1` helps) → green. Do not accept a gate report whose failures do not reproduce sequentially, and do not accept "pre-existing" when a recent run was green.

**Owed before the change is done — three `tech-debt/` files** (individual file + README index line, *not* a DEVLOG note, which archives with the change). All three are pre-existing and explicitly **not owed** by this change; both reviewers and the section-3 supervisor agreed they be carried rather than fixed here:

1. **No full-stack gateway harness** — nothing exercises real gateway → real spawned core → real turn. `Dmon.Network.Tests`' `FakeCoreProcess` replays scripted stdout; the only real `ICoreLauncher` in any test project spawns an actual OS process (`test/Dmon.Core.Tests/Integration/LiveToolCallE2ETest.cs`).
2. **`SpySessionStore` tests are weaker than the `FakeResolver` pattern** — `TurnHandlerIntegrationTests` still leans on fakes where the creating and appending stores never meet. Block 3B's `FakeResolver` (real store, real filesystem, isolated only at the directory-resolution seam) is the better shape.
3. **Half-created session directory on an aborted create** — `SessionStore.CreateAsync` (`core/Dmon.Core/Session/SessionStore.cs:98-102`) creates the directory, `attachments/` and `messages.jsonl` **before** its first `await` at `:114`. A cancellation there orphans a `meta.json`-less directory. Not host divergence (`_currentSession` stays null, so the next turn creates a fresh session), and shared with the explicit path — but the lazy branch makes it reachable **without a user action**. Include the open question of how `ListAsync` behaves on such a directory, and note it is a plausible contributor to the empty-session litter behind design D1.

**Also worth a follow-up note (block 4A reviewer, pre-existing):** the untouched `SessionUpdatedEvent` case (`ConsoleEventHandler.cs:177-179`) also prints under the `[Session]` prefix but in a different grammar — `[Session] {Title}` vs the new `[Session] {Verb}: {Id}`. Two shapes now share one prefix.

**Standing decision worth making consciously (supervisor, section 3).** `CommandDispatcher.DrainAsync` does `Task.WhenAll(_backgroundTasks)` with **no timeout**, and several `CancellationToken.None` emits live inside the turn task — a stuck stdout wedges graceful shutdown. Untouched here, but section 3's fix legitimately relies on that shape being acceptable.

**Nit-level (supervisor, section 3).** `TurnHandlerIntegrationTests.cs` now carries a fourth `ISessionHandler` double, each re-implementing eight no-op members. Ripe for one configurable base next time someone touches the file.
