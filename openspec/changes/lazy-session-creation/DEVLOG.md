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

## NEXT

Section 3, block **3C**: `3.6` — the gateway path (`Dmon.Network` test project). Then the section-3 supervisor review of `24b819b..HEAD`, then section 4.

**`3.6` shape.** Prove that after the gateway's two-step `session.create` → path-less `session.load` handshake, submitting a turn creates **no second session** and emits **no** `sessionStarted`. The handshake is driven by `DriveSessionHandshakeAsync` in `frontends/Dmon.Network/NetworkConnectionEndpoint.cs` (~:525-545), which sends `SessionCreateCommand { Agent = agent }` and awaits the correlated result before the pump starts. Assert absence structurally, as `3.2` does.

**Gate runs must be unsandboxed.** Sandboxed `make build`/`make test` hit the known `dotnet` runfile/NuGet permission artifact — an environment signature, not a code failure.
