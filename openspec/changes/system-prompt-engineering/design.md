## Context

`TurnHandler` currently passes a bare `List<ChatMessage>` (populated only with user/assistant turns) straight to the LLM pipeline. The model has no identity, no behavioural guidelines, and no awareness of the project it is working in. This is the first thing that must be fixed before the agent can be considered a real coding assistant.

The system prompt has two distinct concerns:

1. **Static core** — identity, tool-usage norms, permission model awareness, tone. Changes only when the agent is updated.
2. **Dynamic context** — working directory, platform info, active provider + model, loaded extensions, and optionally a project config file. Assembled fresh each session.

`/reload` triggers a full dmoncore process restart, so no in-session refresh mechanism is needed — a new process rebuilds the prompt from scratch.

## Goals / Non-Goals

**Goals:**
- Give D-mon a consistent identity and terse, informal tone
- Inject working directory, OS/platform, and provider/model into every session
- Discover and include project config (`AGENTS.md` / `CLAUDE.md`) as dynamic context
- Keep `TurnHandler` decoupled from prompt construction via `ISystemPromptBuilder`

**Non-Goals:**
- Skills or agents sections in the prompt (added when those features land)
- Live refresh without restart (handled by process restart on `/reload`)
- Scaffolding an `AGENTS.md` if none is found (deferred to V2 or first extension)
- Structured parsing of `AGENTS.md` — treat it as free-form markdown

## Decisions

### D1: `ISystemPromptBuilder` as a dedicated service

**Decision**: Introduce `ISystemPromptBuilder` (async) in `Dmon.Abstractions`. `TurnHandler` receives it via constructor injection and calls `BuildAsync()` lazily on the first turn, caching the result for the session.

**Alternatives considered**:
- Inline in `TurnHandler` — simpler, but mixes concerns and makes the builder untestable in isolation
- Middleware `IChatClient` decorator — most decoupled, but the system message is part of state (tied to session), not a pipeline concern

### D2: System message as index-0 of `_history`, set once

**Decision**: `BuildAsync()` returns a `ChatMessage(ChatRole.System, text)`. `TurnHandler.SubmitAsync` checks whether `_history` is empty before the first turn and prepends the system message. Subsequent turns skip the build.

**Alternatives considered**:
- Rebuild every turn — unnecessary cost; the prompt doesn't change within a session
- Separate system message field passed to `GetStreamingResponseAsync` — `IChatClient` contract doesn't support this; system messages go in the history list

### D3: Config discovery order

**Decision**: Resolve in this order, combining both user and project configs when both exist:
1. `~/.dmon/AGENTS.md` — user-level preferences, read silently
2. `{CWD}/AGENTS.md` — project config, read silently
3. `{CWD}/CLAUDE.md` — compatibility bridge, only if no `AGENTS.md` found in CWD; emit a `system.notice` event offering to use it

**Rationale**: Separates personal preferences (user config) from project-specific context. The `CLAUDE.md` compat path avoids silently consuming a file that was written for a different agent — surfacing it respects the user's awareness.

### D4: Static core tone and content

**Decision**: The static core text is compiled into the assembly as a string constant (or embedded resource). It is:
- Informal and terse — no corporate hedging, no apologies
- Prescriptive about tool usage: read before editing, prefer targeted edits, ask one short question if scope is unclear
- Explicit about the permission model: bash and file writes require confirmation, the runtime handles it

## Risks / Trade-offs

- **AGENTS.md absence is the common case for now** — dynamic context will be mostly working dir + platform until projects adopt the file. That's fine; it's still better than nothing.
- **Free-form markdown in AGENTS.md** — the agent may receive contradictory or confusing instructions. No mitigation planned; trust the user.
- **System message token cost** — every turn pays for the system message in the context window. Keep the static core concise (~200–400 tokens). Project configs are user-controlled.

## Open Questions

- Should the `system.notice` event for `CLAUDE.md` be a one-shot offer (requiring a confirm response) or just an informational event? Lean toward informational for now — keep it simple.
