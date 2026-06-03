## Context

ADR-003 defined a generic command-response envelope (`{type:"response", command, success, data}`); ADR-015 (Proposed) retires it in favour of dedicated typed events correlated by command `id`. This change implements ADR-015. The full motivation, the rejected polymorphic-data-envelope alternative, and the consequences are in `docs/adrs/ADR-015-typed-command-result-events.md` and the proposal; this document covers the implementation-shaping decisions only.

Current state: `new ResponseEvent` is emitted by exactly one class (`SessionHandler`); `model.*` / `auth.*` already use dedicated typed events but without a correlation id. So the protocol is ~90 % typed-events already, with a session-only generic-envelope island and an inconsistent correlation story.

## Goals / Non-Goals

**Goals:**
- No `object` / `data:any` field in the command-result surface (except the explicitly-deferred `session.getMessages`).
- A single response pattern: every command result is a flat `[JsonDerivedType]` leaf on `Event`, derived from a thin `ResultEvent` base carrying the originating command `id`.
- Uniform, explicit correlation for both success and failure of every command.
- The result surface remains describable by `System.Text.Json`'s `JsonSchemaExporter` with no custom converter — keeping the door open to schema-based client generation (the actual schema-export tooling is a later change).

**Non-Goals:**
- Typing `session.getMessages` / defining the conversation-message DTO — owned by the future turn-persistence change. This change only records the dependency.
- Building the JSON-Schema/AsyncAPI export pipeline or any Swift/Kotlin client. ADR-015 only makes the contract *describable*; generation is downstream.
- Touching streaming/notification events or server-initiated round-trips (`tool.confirmRequest`, `ui.inputRequest`).
- Reworking `ErrorEvent`'s role for ambient (non-command) errors.

## Decisions

### D1 — `ResultEvent` is an abstract base on `Event`, not a mixin or interface

`abstract record ResultEvent : Event` holds the single `[JsonPropertyName("id")] CommandId`. Concrete result events derive from it and remain individual `[JsonDerivedType(..., "<command>Result")]` entries on `Event`. Rationale: the `Event` polymorphic table is the serializer's source of truth and what `JsonSchemaExporter` walks; an intermediate abstract base adds the shared property without adding a second discriminator level. Alternative considered — an `ICorrelated { id }` interface — rejected: interfaces don't participate in the STJ polymorphic contract and wouldn't guarantee the `id` is serialized uniformly.

### D2 — Failures are a correlated `CommandErrorEvent`, not `{success:false}` and not overloaded `ErrorEvent`

`CommandErrorEvent : ResultEvent { command, code, message }`, discriminator `commandError`. `ErrorEvent { code, message, recoverable }` stays as-is for ambient core errors and is **not** reparented. Rationale: command failures must correlate by `id`; ambient errors (e.g. `sessionLocked` surfaced as a notification, extension load failures) have no originating command to correlate to. Keeping them distinct avoids giving `ErrorEvent` a sometimes-null `id` and a confused dual role. The existing `SessionHandler` double-emit (a `ResponseEvent{success:false}` *and* an `ErrorEvent`) collapses: the command failure becomes a single `CommandErrorEvent`; the `sessionLocked` `ErrorEvent` notification is kept where it is genuinely a notification.

### D3 — Naming and array-wrapping

Result event discriminators follow the existing `model.listResult` style: `<command>Result` (`session.createResult`, `session.listResult`, `session.getStatsResult`, …). Bare-array payloads are wrapped in a named field (`session.list` → `{ "sessions": [...] }`) so the type is nameable and pagination has somewhere to live later. `create`/`fork`/`clone`/`load` each get their own event type (all carrying `SessionMeta`) rather than one shared event with a mode field — distinct discriminators let a client switch on `type` without inspecting a payload field, and match the one-event-per-command convention.

### D4 — Retrofit `model.*` / `auth.*` onto `ResultEvent` in the same change

Reparenting `ModelListResultEvent`, `ModelModelsResultEvent`, `AuthStatusResultEvent`, and the auth completion result events onto `ResultEvent` (adding `id`) is in scope, so the change lands one pattern rather than two. Their emit sites (`src/Dmon.Core/Providers`, the auth handler) must thread the originating command `id` through. Rationale: leaving them id-less would preserve the very inconsistency ADR-015 exists to remove, and the remote-gateway correlation requirement applies to them too.

### D5 — `session.getMessages` retains the legacy untyped path, quarantined

`session.getMessages` is the single command allowed to keep returning an untyped payload until the turn-persistence change defines the conversation-message DTO. Implementation choice: rather than keep the full generic `ResponseEvent`, retain the minimum needed for this one command and mark it transitional in code and spec, so the rest of `ResponseEvent` can be deleted. The spec records this as an explicit, bounded exception with the dependency named.

### D6 — Protocol version bump `0.1 → 0.2`

`ProtocolVersion.Current` moves to `"0.2"`. Per ADR-011, `Major.Minor` is the wire contract and this is a breaking-but-pre-1.0 change, so a minor bump is correct. `agentReady` already advertises `protocolVersion`; hosts that pin a version will observe the change.

## Risks / Trade-offs

- **Breaking wire change with in-tree consumers** → The terminal host and all protocol/RPC tests assert the old shape. Mitigation: no external clients exist (V1, single-tenant); update host + tests within this change; the `agentReady` version bump makes the break observable rather than silent.
- **Retrofit (D4) touches provider/auth emit sites that must now have a command `id` in scope** → Risk the `id` isn't readily available at the emit site. Mitigation: the originating `Command.Id` is available on the handled command; thread it through. If an emit site emits a model/auth result *not* in direct response to a command, that surfaces a real design question — stop and resolve rather than inventing an id.
- **`session.getMessages` stays opaque** → A generated client can't type history yet. Mitigation: accepted and bounded by D5; the dependency is recorded so it converts with turn-persistence. This is strictly no worse than today.
- **Two error paths (`CommandErrorEvent` vs `ErrorEvent`)** → Risk of future drift about which to use. Mitigation: the rule is crisp — correlated-to-a-command ⇒ `CommandErrorEvent`; ambient ⇒ `ErrorEvent` — and is stated in the spec.

## Migration Plan

1. Add `ResultEvent` base + `CommandErrorEvent`; register on the `Event` polymorphic table.
2. Add `SessionStats` record and the `Session*ResultEvent` types; register them.
3. Reparent `Model*` / `Auth*` result events onto `ResultEvent`; thread the command `id` through their emit sites.
4. Rewrite `SessionHandler` emit sites to typed events + `CommandErrorEvent`; quarantine `session.getMessages` (D5); delete the now-unused parts of `ResponseEvent`.
5. Bump `ProtocolVersion.Current` to `0.2`.
6. Update the terminal host / RPC client to consume typed events and correlate by `id`.
7. Update protocol-serialization and RPC handler tests.

No runtime rollback concern (no persisted data shape changes); rollback is reverting the change.

## Open Questions

- None blocking. The conversation-message DTO is deferred by design (D5), not an open question for this change.
