## Context

See `proposal.md` — Why. The relevant current state:

- `TurnHandler.PersistNewHistoryEntriesAsync` (`core/Dmon.Core/Rpc/TurnHandler.cs:527`) opens with `if (_sessionStore is null || _sessionHandler.CurrentSession is null) return;` — an unconditional, silent early return.
- `SessionHandler.CreateAsync` (`core/Dmon.Core/Rpc/SessionHandler.cs:36`) is the only caller of `ISessionStore.CreateAsync`. It creates, activates, and emits `session.createResult` as one unit, driven by `CommandDispatcher.cs:124`.
- `ISessionStore.CreateAsync(string? name, string? agent, CancellationToken)` returns `SessionMeta`.
- `agent-core/spec.md` already requires that "before the first turn of each session" the core prepends the assembled system prompt — so session identity is meaningful *during* a turn, not only at its end.
- `Event` is a `[JsonPolymorphic]` hierarchy discriminated by `type` (`core/Dmon.Protocol/Events/Event.cs`); `ResultEvent` is a subtype carrying a command-correlation `id`.

## Goals / Non-Goals

**Goals**

- Every completed turn is persisted, with no path that discards one silently.
- A host can always learn the identity of the session its turns are going to.
- Explicit and implicit session start converge on one host display path.

**Non-Goals**

- Changing when or how sessions are *loaded*, forked, cloned or compacted.
- Any change to the gateway's create handshake.
- Pruning existing empty-session litter (see `proposal.md` — Impact).
- Reworking `/new`. It stays, with its existing meaning.

## Decisions

### D1. Create lazily on first turn, not eagerly at startup

Decided by the Product Owner on evidence: 764 of 769 session directories under the repo's `.dmon/sessions` have an empty `messages.jsonl` (99.3%), and 426 of 787 under `~/.dmon/sessions`. Eager creation at core startup would add one per launch, worsening a measured problem, and would create directories for cores that never run a turn.

*Alternative considered — eager creation at startup.* Simplest invariant (`CurrentSession` is never null, and the guard at :527 could be deleted outright rather than made unreachable), but rejected on the litter evidence above.

*Alternative considered — keep creation explicit and fail loudly.* Smallest change, but it preserves a footgun that costs a whole conversation whenever a user forgets, and it would require amending `session-storage/spec.md` to make persistence conditional — changing an accepted spec to match the code rather than fixing the code.

### D2. Create at turn start, not at persist time

The session is created when a turn is admitted and none is active, before the turn executes — not lazily at the moment of persistence.

Creating at persist time would be a smaller diff but leaves the turn executing with no session identity, which is incoherent with the existing requirement that the system prompt is assembled "before the first turn of each session", and with anything session-scoped during the turn (asset provisioning, activity notification via `ISessionActivityListener`). It would also mean the host learns the session id only after the answer has streamed, which is precisely backwards for a user who wants to know where their conversation is going.

### D3. A new non-command event rather than reusing `SessionCreatedResultEvent`

ADR-015 is explicit that `ResultEvent` is a command response — "`id` correlates to the command", and success and failure both correlate. An implicitly created session has no originating command, so emitting a `ResultEvent` for it would put a correlation id on the wire that matches nothing, contradicting the ADR's model.

ADR-015 also already establishes the non-command event class: it retains `ErrorEvent` "for **non-command** core errors (it is not a `ResultEvent`)". A `sessionStarted` event sits in exactly that class, so this extends the accepted model rather than amending it and **no superseding ADR is required**. Confirm this reading holds before implementing; if it does not, stop — the path is a superseding ADR, not a workaround.

*Alternative considered — reuse `SessionUpdatedEvent {id, title}`.* Already non-command, needs no new type and no schema change, but "updated" misdescribes a creation and would overload one event with two meanings for the sake of avoiding an additive change.

### D4. Extract a shared create-and-activate seam

`SessionHandler.CreateAsync` currently fuses three things: create, activate, and emit `session.createResult`. The implicit path needs the first two and must **not** emit the result event (there is no command to correlate to), emitting `sessionStarted` instead.

Extract the create-and-activate step into one internal seam used by both paths, so the two cannot diverge in activation semantics, `meta.json` contents, or activity notification. The emitted event stays the caller's decision. This is what makes the spec's "indistinguishable from an explicitly created session" requirement structural rather than a promise.

### D5. Bind the implicit session to the running agent

`ISessionStore.CreateAsync` takes an `agent`. The implicit path passes the agent the core is already running — the same value an explicit `session.create` would resolve to by default. Any other choice would make an implicitly created session behave differently on reload, which D4 exists to prevent.

### D6. One display path in the console host

`ConsoleEventHandler` currently calls `TrackActiveSession(...)` from four separate cases (`created`, `forked`, `cloned`, `loaded`), none of which displays anything. Rather than adding a print to the `created` case alone, give `TrackActiveSession` the display responsibility so every route that makes a session active surfaces it, and have the new `sessionStarted` case join them. This satisfies the existing `console-host` display requirement and the new one with a single change, and removes the class of bug where one route is silent.

## Risks / Trade-offs

- **A session is now created by simply typing, so light/exploratory use leaves directories behind** → Accepted, and strictly better than today: a directory appears only when something was actually said, whereas today the store is 99.3% empty directories created by other means. Pruning is a separate change.
- **`sessionStarted` reaches hosts that do not know it** → Verified safe before proposing: `RpcTransportExtensions.cs:83-85` skips events that are not a `ResultEvent` matching a pending command id; `dmon-home` projects unrecognised events to `nil`; `Dmon.Desktop` switches on known types. Add a regression test for the request/response tolerance rather than relying on inspection.
- **The guard at `TurnHandler.cs:527` becomes unreachable rather than removed** → Keep it as a defensive assertion but make it *loud* (log a warning) instead of silent, so that if a future path ever reaches it the failure is visible. A silent guard is what caused this defect; replacing it with another silent guard would repeat it.
- **Schema export drifts and the freshness gate goes red** → The gate is the mitigation; regenerating the export is an explicit task, not a side effect.
- **Time pressure** (wanted before an evening talk) → The change is additive and the risky part (wire shape) was settled before proposing. If it cannot land cleanly, the demo does not depend on it — the fallback is simply typing `/new`.

## Migration Plan

No migration. There are no production deployments, the wire change is additive, and existing sessions are untouched. Rollback is reverting the change; sessions created implicitly beforehand remain valid because D4/D5 make them identical to explicitly created ones.

## Open Questions

None. The two that existed — lazy vs eager creation, and how to announce an uncommanded session — were resolved with the Product Owner before this change was proposed (D1, D3).
