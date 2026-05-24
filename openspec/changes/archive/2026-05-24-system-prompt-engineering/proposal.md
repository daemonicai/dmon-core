## Why

D-mon currently sends zero context to the LLM — no identity, no behavioural guidelines, no project awareness. The model operates blind, producing inconsistent and unguided behaviour. A system prompt is the foundation every coding agent capability builds on.

## What Changes

- Introduce `ISystemPromptBuilder`, an async service that assembles the system prompt at session start
- Add a static core: identity, tool usage norms, permission model awareness, tone
- Add dynamic context assembly: working directory, OS/platform, active provider + model, loaded extensions
- Implement project config discovery (`~/.dmon/AGENTS.md` user prefs, `{CWD}/AGENTS.md` project config, `{CWD}/CLAUDE.md` compat fallback with offer)
- Prepend the assembled `ChatMessage(ChatRole.System, …)` to `_history` before the first turn in `TurnHandler`

## Capabilities

### New Capabilities

- `system-prompt`: The assembled system prompt — static core, dynamic context, and project config combined into the first `ChatRole.System` message in conversation history

### Modified Capabilities

- `agent-core`: `TurnHandler` gains a dependency on `ISystemPromptBuilder`; history initialisation changes to prepend the system message

## Impact

- `Dmon.Core` — new `ISystemPromptBuilder` interface and implementation; `TurnHandler` modified
- `Dmon.Abstractions` — `ISystemPromptBuilder` interface may live here
- No RPC protocol changes
- No breaking changes to existing tool or provider surface
