# ADR-015: Typed Command-Result Events; Retire the Generic `response` Envelope

**Date:** 2026-06-01
**Status:** Accepted
**Amends:** ADR-003 (the "Responses (core → host)" framing; the response-shape notes in the Message Surface tables)

## Context

ADR-003 defines a generic command-response envelope:

```json
{"id": "req-1", "type": "response", "command": "session.list", "success": true, "data": {...}}
{"id": "req-1", "type": "response", "command": "session.load", "success": false, "error": "description"}
```

In code this is `ResponseEvent`, whose payload is `object? Data`. Three problems follow from the `object?`:

1. **The binding "command → payload type" exists nowhere declarative.** It lives only in imperative handler code — `SessionHandler` sets `Command = "session.list"` and `Data = sessions` at the same call site. No schema tool, including `System.Text.Json`'s `JsonSchemaExporter`, can recover that association.
2. **Some payloads have no named type at all.** `session.getStats` returns an *anonymous* object (`{tokens, cost, contextUsage, currentModel}`) that exists for exactly one expression; `session.getMessages` returns `IReadOnlyList<object>` — the element type is erased even in C#.
3. **The envelope is already an outlier.** A sweep of the core shows `new ResponseEvent` is emitted by **exactly one class** (`SessionHandler`). Every other command that returns data already emits a **dedicated typed event** — `model.list` → `ModelListResultEvent`, `model.models` → `ModelModelsResultEvent`, `auth.status` → `AuthStatusResultEvent`. The generic `response` envelope is a session-only island; the rest of the protocol converged on typed events organically.

This matters now because of two accepted directions:

- **ADR-011** commits to granular contract packages and a wire protocol versioned by `Major.Minor`. A wire contract that cannot be machine-described is not really a contract.
- **ADR-012** puts a single-tenant remote gateway in scope, with **non-.NET clients** (a personal iOS/Swift client; Kotlin is plausible). Those clients should be **generated or validated against a schema**, not hand-transcribed. An `object?` / `data: any` payload cannot be generated and cannot be schema-checked.

There is a second gap the existing typed events expose. `ResponseEvent` carries the originating command `id`, but the typed result events do **not**:

```csharp
ModelListResultEvent : Event { models, activeProvider, activeModelId }   // no id
AuthStatusResultEvent : Event { providers }                              // no id
```

They are correlated only by type + ordering. That is tolerable for the local TUI (one interactive command at a time) but **risky for the resumable remote gateway** (ADR-012 / ADR-014): a client that retries a command after a reconnect cannot match a `*Result` to the attempt that produced it. The remote scenario raises the value of correlation exactly where the existing typed-event pattern dropped it. `ErrorEvent` (`{code, message, recoverable}`) likewise carries no `id`, so failures are uncorrelated too.

Two shapes were considered to close the `object?` hole:

1. **Polymorphic-data envelope** — keep `ResponseEvent`, make `data` a polymorphic type discriminated by `command`. Either the payload duplicates the `command` discriminator inside `data`, or it needs a hand-rolled `JsonConverter` the schema exporter cannot see through. Keeps two response patterns alive.
2. **Typed events everywhere** — finish the migration the codebase is already 90 % through: every command result is a flat `[JsonDerivedType]` leaf on `Event`.

## Decision

**Retire the generic `response` envelope. Every command result is a dedicated typed event, correlated by the originating command `id`.**

### 1. No opaque payloads on the wire

`ResponseEvent` and the `{type: "response", data: ...}` shape are removed. There is no `object` / `data: any` field anywhere in the command-result surface. Every result field is named and typed, so the whole surface is reachable from the `Command` / `Event` `[JsonPolymorphic]` tables and describable by `JsonSchemaExporter`.

### 2. A thin `ResultEvent` correlation base

A new base type sits between `Event` and the concrete command-result events, carrying only the correlation id:

```csharp
public abstract record ResultEvent : Event
{
    [JsonPropertyName("id")]
    public required string CommandId { get; init; }   // echoes the originating Command.Id
}
```

All command-result events derive from `ResultEvent`. This recovers the correlation the retired envelope provided and makes it **uniform** — strictly better than the previous typed events, which had none. Concrete leaves remain `[JsonDerivedType]` entries on `Event`, so the schema exporter still describes each one natively; `id` is just a property.

**Streaming and notification events are *not* command results and remain id-less** — `turnStart`, `messageStart`/`messageDelta`/`messageEnd`, `toolExecutionStart`/`End`, `sessionUpdated`, `compactionStart`/`End`, `retryAttempt`, etc. do not derive from `ResultEvent`. Server-*initiated* round-trips (`tool.confirmRequest`, `ui.inputRequest`) keep their own server-generated request id as today.

### 3. Failures are correlated too

`{success: false, error}` is replaced by a single correlated error result:

```csharp
public sealed record CommandErrorEvent : ResultEvent   // type: "commandError"
{
    [JsonPropertyName("command")] public required string Command { get; init; }
    [JsonPropertyName("code")]    public required string Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
}
```

`ErrorEvent` (`{code, message, recoverable}`) is retained for **non-command** core errors (it is not a `ResultEvent`). Success and failure of a command now both correlate by `id`.

### 4. Retrofit the existing typed events onto the base

`ModelListResultEvent`, `ModelModelsResultEvent`, `AuthStatusResultEvent`, and any other command-result event are reparented to `ResultEvent` (gaining `id`). After this change there is **one** response pattern in the protocol, not two.

### 5. Name the payloads; defer the one that isn't ready

- `session.getStats` → a named `SessionStats` record (`{tokens, cost, contextUsage, currentModel}`), carried by `SessionStatsResultEvent`.
- `session.create` / `fork` / `clone` / `load` → result events carrying `SessionMeta`.
- `session.list` → `SessionListResultEvent { id, sessions: SessionMeta[] }`. The previously bare array is **wrapped** in a named field (room for pagination/cursors later).
- `session.getMessages` → **deferred.** Its payload is the conversation-message DTO, which does not yet exist as a stable named type: the code persists a minimal placeholder (`TurnLineRecord` — text-only `{entryId, timestamp, role, text, scope}`) that **diverges from the `session-storage` spec** (which specifies `UserMessage` / `AssistantMessage` / `ToolResultMessage` records) and is explicitly marked transitional pending the canonical turn-persistence change. `session.getMessages` is the **sole** command that may retain a legacy untyped path until that change lands and defines the DTO; it is then converted to `SessionMessagesResultEvent`.

### 6. Wire shape

```json
{"type": "session.listResult",  "id": "req-1", "sessions": [ ... ]}
{"type": "session.getStatsResult", "id": "req-2", "stats": {"tokens": 0, "cost": 0, "contextUsage": 0, "currentModel": null}}
{"type": "commandError", "id": "req-3", "command": "session.fork", "code": "noActiveSession", "message": "No active session to fork."}
```

Discriminator is `type` (as for every event); `id` correlates to the command. Naming convention follows the existing `model.listResult` / `auth.statusResult` style: `<command>Result`.

### Why typed events, not the polymorphic-data envelope

- Typed events are the **house style already** — the envelope was the lone exception.
- Flat `[JsonDerivedType]` leaves are described by `JsonSchemaExporter` with **no custom converter and no duplicated/nested discriminator**. The polymorphic-data envelope needs one or the other, and a hand-rolled converter is opaque to the schema exporter — defeating the entire purpose (a generatable contract).
- It **deletes** the opaque field rather than decorating it.

## Consequences

- **The wire protocol becomes fully machine-describable.** A build/test-time generator can walk the `Command` / `Event` polymorphic tables via `JsonSchemaExporter` and emit a schema; a golden-file CI test fails on drift. Non-.NET clients (Swift `Codable`, Kotlin `kotlinx.serialization` — whose class-discriminator model maps almost 1:1 onto `[JsonPolymorphic]`) can be generated or validated against it. This ADR is the prerequisite that makes the "generate the client from a schema" path honest.
- **Breaking wire change.** Clients reading `{type: "response", ...}` must migrate to per-command result events. Per ADR-011 (`Major.Minor` = wire contract), this is a **minor protocol bump** (`ProtocolVersion` `0.1` → `0.2`). There are no external clients yet (V1, single-tenant), so the blast radius is the in-tree terminal host plus tests.
- **`object` opacity is eliminated** from the command-result surface except for the explicitly-deferred `session.getMessages`, which is gated on the turn-persistence change.
- **Correlation is uniform and explicit.** Every command result — success or error — echoes the command `id`, so concurrent or replayed commands over the resumable gateway (ADR-012/014) are matched unambiguously. This removes a latent reconnect-correlation bug from the remote path.
- **One response pattern.** `model.*` / `auth.*` results join the same `ResultEvent` base; the protocol no longer has a session-only special case.
- **Bare arrays become named fields** (`session.list` → `{sessions: [...]}`), giving future pagination somewhere to live.
- **Supersedes the ADR-003 response framing.** ADR-003's "Responses (core → host)" subsection and the implied `{success, data}` response shape in its Message Surface tables no longer describe the wire; this ADR does. ADR-003's command tables, event tables, turn lifecycle, dispatch concurrency, and tool-confirmation sections are unaffected.
- **Spec sync required.** The OpenSpec specs that document the response/event shapes must be updated in the accompanying change, and the `session-storage` spec/code divergence around the message DTO is called out as the dependency that blocks `session.getMessages`.
