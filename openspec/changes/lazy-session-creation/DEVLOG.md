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


## NEXT

Section 1 is closed pending the `[supervisor]` review of `253060f..HEAD`. Then section 2: the create-and-activate seam (`2.1`–`2.2`).

**Carried into §2/§3 — design D5 resolved by the architect.** D5 binds the implicit session to "the agent the core is already running". Tracing it: the core process carries **no agent name**. `agent` is a per-command `string?` the host supplies on `SessionCreateCommand`; `Dmon.Terminal` supplies nothing (so `/new` creates with `agent: null`), and the gateway supplies `createFrame.Agent` — but the gateway's two-step create -> path-less load handshake means the lazy path never fires there. So the implicit path passes **`null`**, which is D5's own second clause ("the same value an explicit `session.create` would resolve to by default") and satisfies D5's stated purpose exactly: an implicitly created session's `meta.json` is byte-identical to an explicitly created one from the same host. Inventing any other value (e.g. `"default"`) would be precisely the divergence D5 exists to prevent. The seam still takes `string? agent` so a future in-core agent identity has somewhere to go.

**Insertion point for `3.1`** (established while briefing): `TurnHandler.SubmitAsync` (`core/Dmon.Core/Rpc/TurnHandler.cs:112`), immediately after the `_turnGate` is acquired and `_turnCts` is created, and **before** `NotifyTurnStarted(...)` at :128 — because that call and the asset provisioning and system-prompt build that follow it all read `_sessionHandler.CurrentSession?.Id`. Creating any later than this would hand them a null session id and violate design D2.
