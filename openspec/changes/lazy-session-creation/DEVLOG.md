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

## NEXT

Section 2 is closed pending its `[supervisor]` review of `84530c7..HEAD`. Then section 3 — the actual defect fix.

**Owed by the architect, landing with section 3:** update `docs/protocol/README.md` §5.3 to record that a session-less first turn is preceded by `sessionStarted` (supervisor note 1 on section 1).

**Carry into section 3 — the reviewer's nit on block 2A.** The two `TurnHandlerIntegrationTests.cs` fakes' `CreateAndActivateAsync` does **not** invoke any `ISessionActivityListener`, and neither fake takes one. That is fine for what section 3 needs (`TurnHandler` does not consume listeners directly), but it is a point where the fake's observable behaviour diverges from the real seam's, so a section-3 test written only against the fake would not catch a listener regression. Brief section 3 accordingly.

**Design D5 resolved by the architect** (see the section-2 base post below for the full reasoning): the implicit path passes `agent: null`.

**Insertion point for `3.1`**: `TurnHandler.SubmitAsync` (`core/Dmon.Core/Rpc/TurnHandler.cs:112`), immediately after the `_turnGate` is acquired and `_turnCts` is created, and **before** `NotifyTurnStarted(...)` at :128 — that call, plus the asset provisioning and system-prompt build that follow it, all read `_sessionHandler.CurrentSession?.Id`. Creating any later would hand them a null session id and violate design D2.

**Gate runs must be unsandboxed.** Sandboxed `make build`/`make test` hit the known `dotnet` runfile `build-start.cache` permission artifact — an environment signature, not a code failure.
