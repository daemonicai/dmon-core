# ADR-003: RPC Surface

**Date:** 2026-05-22
**Status:** Accepted

## Context

The agent core runs as a separate process and communicates with host frontends (console/TUI, Avalonia desktop, third-party clients) over stdio. The brief specifies JSON-RPC over stdio and cites Pi (`earendil-works/pi`) as a reference.

Two protocol shapes were considered:

1. **Strict JSON-RPC 2.0** — spec-compliant, with the `jsonrpc` envelope, batch requests, and standardised error objects.
2. **Pi-style JSONL** — Pi's custom format: newline-delimited JSON, commands in one direction, events the other, no JSON-RPC envelope.

Pi's full RPC surface was reviewed as a reference.

## Decision

**Adopt Pi's protocol shape, not strict JSON-RPC 2.0.**

JSON-RPC 2.0 compliance adds ceremony (the `jsonrpc` envelope, batch request handling, error object structure) with no practical benefit for a single-connection stdio protocol. Staying close to Pi's shape means Pi frontends (e.g. `pi-coding-agent` for Emacs) need minimal adaptation to talk to daemon.

### Framing

JSONL over stdio, with strict LF (`\n`) line delimiters. Clients must strip trailing `\r`.

Commands (host → core):

```json
{"id": "req-1", "type": "commandName", ...params}
```

Responses (core → host, for commands that need acknowledgement):

```json
{"id": "req-1", "type": "response", "command": "commandName", "success": true, "data": {...}}
{"id": "req-1", "type": "response", "command": "commandName", "success": false, "error": "description"}
```

Events (core → host, no `id`):

```json
{"type": "eventName", ...payload}
```

### Turn lifecycle

Commands are fire-and-forget with an event stream back. `turn.submit` does not return a long-running response — the host processes the event stream. One turn = one LLM call + tool executions + tool results. `turnEnd` includes the complete assistant message and all tool results.

### Tool confirmation

The permission gate middleware uses Pi's `extension_ui_request / extension_ui_response` pattern. When the gate needs confirmation before invoking a tool, the core emits a `tool.confirmRequest` event; the host responds with a `tool.confirmResponse` command. The core suspends the turn until the response arrives. The same mechanism supports richer interactions (`select`, `input`, `editor`) for tools that need host UI.

Tool confirmation request (core → host):

```json
{"type": "tool.confirmRequest", "id": "uuid-1", "name": "writeFile", "args": {...}, "risk": "high"}
```

Tool confirmation response (host → core):

```json
{"type": "tool.confirmResponse", "id": "uuid-1", "confirmed": true}
{"type": "tool.confirmResponse", "id": "uuid-1", "cancelled": true}
```

`risk` is an enum: `none | low | high`. Hosts use this to decide how to present the confirmation (e.g. the Avalonia host may show a visual diff for `high`-risk file operations).

### Capability negotiation

Provider capability differences (tool-calling support, context window, reasoning, supported input types) are encoded in the `Model` object returned by `model.list`. This is the source of truth for what the current provider can do; the agent loop reads it rather than maintaining a separate capability registry.

```json
{
  "id": "claude-sonnet-4-6",
  "name": "Claude Sonnet 4.6",
  "provider": "anthropic",
  "baseUrl": "https://api.anthropic.com",
  "reasoning": true,
  "input": ["text", "image"],
  "toolCalling": true,
  "contextWindow": 200000,
  "maxTokens": 16384
}
```

## Message Surface

> Auth-related commands and events (`auth.login`, `auth.logout`, `auth.status`, `auth.loginComplete`, `auth.logoutComplete`, `auth.loginFailed`, `auth.statusResult`) are defined in ADR-005 and follow the same envelope as everything below.

### Host → Core (commands)

| Command | Parameters | Notes |
|---------|-----------|-------|
| `session.create` | — | |
| `session.fork` | `entryId` | |
| `session.clone` | — | |
| `session.load` | `path` | |
| `session.list` | — | |
| `session.setName` | `name` | |
| `session.getStats` | — | Returns `{tokens, cost, contextUsage, currentModel}` |
| `session.getMessages` | — | Returns full message history for the current session |
| `session.compact` | — | Triggers compaction immediately; emits `compactionStart` / `compactionEnd` |
| `turn.submit` | `message`, `images?` | |
| `turn.steer` | `message`, `images?` | Queued after current tool execution |
| `turn.followUp` | `message`, `images?` | Queued after agent finishes |
| `turn.abort` | — | |
| `tool.confirmResponse` | `id`, `confirmed` / `cancelled` | Response to a `tool.confirmRequest` |
| `ui.inputResponse` | `id`, `value` / `cancelled` | Response to a `ui.inputRequest` |
| `model.set` | `provider`, `modelId` | |
| `model.cycle` | — | |
| `model.list` | — | Returns `Model[]` |
| `extension.load` | `source` | Path or NuGet package id |
| `extension.unload` | `name` | Deregisters the extension's tools; assemblies remain resident until the core process restarts |
| `extension.promote` | `name` | Scaffolds .csx into csproj |
| `thinking.set` | `level` (`off`/`low`/`medium`/`high`) | |
| `thinking.cycle` | — | |

### Core → Host (events)

| Event | Payload | Notes |
|-------|---------|-------|
| `agentReady` | `protocolVersion`, `coreVersion` | First event emitted on startup, before any command is processed |
| `agentStart` | — | Per-turn marker |
| `agentEnd` | `messages` | Per-turn marker |
| `bootstrapNotice` | `path`, `created[]` | Emitted when `.dmon/` is auto-created on first use |
| `providerSwitched` | `name`, `model`, `effectiveNextTurn` | `effectiveNextTurn: true` — in-flight turn finishes on previous provider |
| `capabilityIgnored` | `capability`, `requestedValue`, `reason` | E.g. `thinking.set` against a provider without reasoning support |
| `extensionError` | `source`, `phase`, `diagnostics[]` | Script compile errors, NuGet load errors, etc. |
| `retryAttempt` | `attempt`, `maxAttempts`, `nextDelayMs`, `reason` | Emitted before each transient-error retry |
| `error` | `code`, `message`, `recoverable` | Non-fatal core errors surfaced to the host |
| `ui.inputRequest` | `id`, `kind` (`text`/`secret`/`select`), `prompt`, `options?` | Dedicated UI input channel (distinct from `tool.confirmRequest`) |
| `turnStart` | — | |
| `turnEnd` | `message`, `toolResults` | |
| `messageStart` | `message` | |
| `messageDelta` | `message`, `delta` | See delta types below |
| `messageEnd` | `message` | |
| `toolExecutionStart` | `callId`, `name`, `args` | |
| `toolExecutionEnd` | `callId`, `result`, `isError` | |
| `tool.confirmRequest` | `id`, `name`, `args`, `risk` | Core suspends until `tool.confirmResponse` |
| `sessionUpdated` | `id`, `title` | |
| `extensionLoaded` | `name`, `tools[]` | |
| `extensionUnloaded` | `name` | |
| `compactionStart` | `reason` | |
| `compactionEnd` | `reason`, `result`, `aborted` | |

### `messageDelta` delta types

| Type | Fields | Notes |
|------|--------|-------|
| `start` | — | |
| `textStart` | — | |
| `textDelta` | `delta`, `partial` | Streaming text chunk |
| `textEnd` | `content` | |
| `thinkingStart` | — | |
| `thinkingDelta` | `delta` | |
| `thinkingEnd` | `content` | |
| `toolCallStart` | — | |
| `toolCallDelta` | `delta` | Arguments streaming |
| `toolCallEnd` | `toolCall` | |
| `done` | `reason` (`stop`/`length`/`toolUse`) | |
| `error` | `reason` | |

### Single-instance enforcement

The agent core is a single-connection-per-process server. Two enforcement layers:

1. **stdio model.** Stdio is intrinsically point-to-point — only the parent that spawned the core can speak to it. No additional locking is required for the stdio transport.
2. **Session directory lock.** When the core opens a session, it acquires an exclusive advisory lock on `<session-id>/.lock` (POSIX `flock` / Windows `LockFileEx`). A second core attempting to open the same session releases its handle and exits with `error: sessionLocked`. The lock is released on clean shutdown and on process exit (OS-level cleanup).

The session lock — not a global daemon lock — is what prevents concurrent writes to `messages.jsonl`.

### Transient-error retry policy

Provider calls that fail with retryable errors (HTTP 5xx, 429 rate-limit, provider-specific `overloaded`) are retried by the core, transparently to the host:

- **Strategy:** exponential backoff with jitter — `delay = min(maxDelay, baseDelay * 2^attempt) ± 25%`.
- **Defaults:** `baseDelay = 1s`, `maxDelay = 30s`, `maxAttempts = 5`. All overridable via `IConfiguration` under `Daemon:Provider:Retry:*`.
- **Honour `Retry-After` headers** when present — they replace the computed backoff for that attempt.
- **Per-attempt event:** the core emits `retryAttempt {attempt, maxAttempts, nextDelayMs, reason}` so the host can show progress.
- **Non-retryable errors** (4xx other than 408/429, auth failures) fail the turn immediately with a `turnEnd {stopReason: error}`.

## Consequences

- **Pi frontends need minimal adaptation.** The protocol shape is close enough that clients written for Pi can talk to daemon with a thin adapter.
- **bash is not a top-level command.** Pi surfaces bash as a first-class RPC command because its TypeScript runtime needs it. dmon's bash tool is an `AIFunction` extension that goes through the same path as all other tools, including the permission gate. No special case at the protocol level.
- **Tool confirmation is not a separate channel.** The `tool.confirmRequest / tool.confirmResponse` pattern composes with any future host UI interaction (select, input, editor) using the same mechanism.
- **camelCase throughout.** Diverges from Pi's `snake_case` to match .NET/JSON conventions.
- **No JSON-RPC 2.0 compliance.** Tooling that expects a strict JSON-RPC 2.0 server will not work without an adapter.
