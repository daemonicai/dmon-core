## Why

A turn submitted when no session is active is **silently discarded**. `TurnHandler.PersistNewHistoryEntriesAsync` (`core/Dmon.Core/Rpc/TurnHandler.cs:527`) returns early when `ISessionHandler.CurrentSession` is null — no log, no warning, no host notice — and nothing in the core ever creates a session implicitly. `SessionCreateCommand` is reachable only from the console host's `/new` slash command (`frontends/Dmon.Terminal/SlashCommandParser.cs:59-63`), so a user who starts the TUI and simply types loses the entire conversation without being told.

This is not a design decision that was recorded anywhere; it contradicts an accepted spec. `session-storage/spec.md` requires unconditionally that "at turn completion the orchestration point SHALL append the turn(s) to `messages.jsonl` via session-storage" — there is no "if a session is active" qualifier — and `agent-core/spec.md` requires tool calls and results be persisted "not discarded after the turn". The defect was found live: a real tool-calling turn ran to completion, produced a correct answer, and persisted nothing.

A second, compounding defect sits alongside it: `/new` itself is silent. `ConsoleEventHandler.cs:208` handles `SessionCreatedResultEvent` by tracking the session internally and printing nothing, violating `console-host/spec.md`'s requirement that the host "displays the new session context". The one command a user must know to invoke, in order to avoid silent data loss, gives no sign it worked.

## What Changes

- **The core creates a session lazily, on the first turn that needs one.** When a turn is submitted with no active session, the core creates and activates one before the turn is persisted, making session-storage's unconditional requirement literally true.
  - Creation is **lazy, not eager**. Sessions are not created at core startup. The store already carries heavy empty-session litter — 764 of 769 directories under the repo's `.dmon/sessions` have an empty `messages.jsonl`, and 426 of 787 under `~/.dmon/sessions` — and eager creation would add one per launch regardless of whether anything was said.
- **A new non-command event, `sessionStarted`, carrying `SessionMeta`**, announces a session the host never asked for. Reusing `SessionCreatedResultEvent` would contradict ADR-015, which is explicit that `ResultEvent` is a response to a command correlated by `id`; an implicit creation has no originating command. ADR-015 already establishes the non-command event class by retaining `ErrorEvent`, so this extends the existing model rather than amending it.
- **The console host displays the new session context** for both explicit (`/new`) and implicit (`sessionStarted`) creation, through one shared display path so the behaviour cannot drift between them.
- `/new` is **retained** — it keeps the distinct meaning "start a fresh session now, discarding current context".
- Not breaking: the addition is additive on the wire, and the gateway's explicit two-step create → path-less load handshake means `CurrentSession` is never null on that path, so the lazy path never fires there.

## Capabilities

### New Capabilities

None. This change corrects conformance against existing capabilities and extends the core's RPC surface.

### Modified Capabilities

- `agent-core`: new requirement that a turn submitted with no active session causes the core to create and activate one before persisting, and to emit a `sessionStarted` event carrying the new `SessionMeta`. Adds `sessionStarted` to the documented event surface as a non-command event.
- `console-host`: the host displays the session context on implicit session start as well as on `/new`, satisfying the existing display requirement through a path shared by both.

## Impact

- **Code**: `core/Dmon.Core/Rpc/TurnHandler.cs` (lazy creation at turn start; the silent guard at :527 becomes unreachable for want of a session), `core/Dmon.Core/Rpc/SessionHandler.cs` (activation seam), `core/Dmon.Protocol/Events/` (new `SessionStartedEvent` + `Event` discriminator registration), `frontends/Dmon.Terminal/ConsoleEventHandler.cs` (shared display path).
- **Wire protocol**: additive. The machine-readable schema export must be regenerated so the `protocol-schema` freshness gate stays green.
- **Other hosts**: no change required. `Dmon.Desktop` switches on known event types; `dmon-home` projects unrecognised events to `nil`. `RpcTransportExtensions` skips events that are not a `ResultEvent` matching a pending command id, so the request/response path is undisturbed.
- **Explicitly out of scope**: pruning the accumulated empty-session litter. That needs its own change, and should begin by establishing *what* creates those directories — the test suite writing into the repo's `.dmon` is a plausible cause — before any deletion policy is designed.
