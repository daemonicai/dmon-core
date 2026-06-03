## Why

The command-result surface uses a generic envelope — `{type:"response", command, success, data}` — whose payload is `object?`. The binding "command → payload type" exists only in imperative handler code, so no schema tool can describe it; `session.getStats` returns an anonymous type and `session.getMessages` an `IReadOnlyList<object>`. This is incompatible with two accepted directions: ADR-011's machine-describable, versioned wire contract, and ADR-012's non-.NET remote clients (a Swift/iOS client, plausibly Kotlin) that should be **generated or validated against a schema**, not hand-transcribed. Separately, the typed result events the codebase already uses (`ModelListResultEvent`, `AuthStatusResultEvent`, …) carry **no correlation id**, which is risky for ADR-012/014 resumable sessions where a reconnecting client may replay a command and cannot match the result to the attempt. ADR-015 resolves both.

## What Changes

- **BREAKING:** Retire `ResponseEvent` and the `{type:"response", data:...}` wire shape. No `object`/`data:any` field remains in the command-result surface.
- Introduce a thin `ResultEvent : Event` base carrying the originating command `id`. Every command-result event derives from it — uniform, explicit correlation for success and failure.
- Add `CommandErrorEvent : ResultEvent { id, command, code, message }` for correlated command failures. `ErrorEvent` is retained for non-command (ambient) core errors and does **not** become a `ResultEvent`.
- Replace the session-only generic responses with dedicated typed events: `session.create/fork/clone/load` → events carrying `SessionMeta`; `session.list` → `SessionListResultEvent` wrapping `{sessions:[...]}` (bare array becomes a named field); `session.getStats` → `SessionStatsResultEvent` carrying a new named `SessionStats` record.
- Retrofit the existing `model.*` / `auth.*` result events onto `ResultEvent` (they gain `id`), leaving the protocol with **one** response pattern.
- **Defer `session.getMessages`.** Its payload is the conversation-message DTO, which does not yet exist as a stable named type — the persisted `TurnLineRecord` is a text-only placeholder that diverges from the `session-storage` spec. `session.getMessages` is the **sole** command permitted to retain the legacy untyped path until the canonical turn-persistence change defines the DTO; it converts then.
- **BREAKING:** Minor protocol bump `0.1 → 0.2` (`ProtocolVersion.Current`), per ADR-011 (`Major.Minor` = wire contract).
- Streaming/notification events (`turnStart`, `messageDelta`, `sessionUpdated`, …) are unchanged and remain id-less; server-initiated round-trips keep their own request ids.

## Capabilities

### New Capabilities
<!-- None — this change modifies the existing command-result contract; it adds no new capability. -->

### Modified Capabilities
- `agent-core`: the command-result contract — retire the `response` envelope; introduce the `ResultEvent` correlation base and `CommandErrorEvent`; `session.getStats` returns a typed `SessionStats` event; `session.getMessages` is explicitly deferred (the one sanctioned legacy path) pending the turn-persistence DTO.
- `provider-model-listing`: `ModelListResultEvent` and `ModelModelsResultEvent` gain the correlation `id` (reparented onto `ResultEvent`). (The `model-switcher` terminal picker requirements are unchanged — the new `id` field is additive and does not alter "show picker on event arrival".)
- `auth`: `auth.statusResult` and the auth completion events are reparented onto `ResultEvent` and gain `id`.

## Impact

- **Code:** `src/Dmon.Protocol` — remove `ResponseEvent`; add `ResultEvent` base, `CommandErrorEvent`, the `Session*ResultEvent` types, the `SessionStats` record; reparent `Model*ResultEvent` / `Auth*` result events; update the `[JsonDerivedType]` tables on `Event`. `src/Dmon.Core/Rpc/SessionHandler.cs` — emit typed events instead of `ResponseEvent`/`ErrorEvent`-for-command-failures. `src/Dmon.Protocol/ProtocolVersion.cs` — bump to `0.2`.
- **Hosts:** `src/Dmon.Terminal` (and any RPC client) — consume the per-command result events and correlate by `id` instead of reading `{type:"response", data}`.
- **Tests:** all protocol/serialization and RPC handler tests asserting the `response` shape.
- **Docs/specs:** ADR-015 (already drafted) governs; the modified capability specs above are updated in this change.
- **Out of scope / dependency:** `session.getMessages` typing and the conversation-message DTO are owned by the future turn-persistence change; this change only records the dependency and the spec/code divergence.
- **No external clients exist yet** (V1, single-tenant), so the breaking wire change's blast radius is in-tree only.
