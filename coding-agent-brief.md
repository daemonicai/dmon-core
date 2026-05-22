# .NET-native Coding Agent — Explore Brief

## Vision

A .NET-native coding agent inspired by [Pi](https://github.com/earendil-works/pi)
(formerly `pi-mono`), built in C# on .NET 10. The agent should feel like Pi —
self-extensible, multi-provider, session-as-data — but be designed natively for
the .NET runtime and ecosystem rather than ported from TypeScript. Two host
surfaces planned: a console/TUI host for everyday use, and an Avalonia desktop
host for the UI affordances a terminal can't easily provide.

This is "inspired by", not a port. We keep what makes Pi good, drop what's
TypeScript-shaped, and lean into things .NET does well.

## Core Architectural Decisions

### Agent core as a separate process, exposed over JSON-RPC

The agent runtime lives in its own process and speaks JSON-RPC over stdio (Pi's
model). Both hosts (console + Avalonia) are thin frontends over that RPC.
Benefits:

- Console and desktop hosts share zero UI code but share all agent behaviour.
- Third-party frontends (Emacs, VS Code, web) can be built later without
  touching the core.
- Remote operation, sandboxing, and multi-agent-from-one-UI become possible
  without re-architecting.

Reject the alternative of agent-core-as-shared-library — it's simpler on day
one but boxes us in fast.

### Two-tier extension model

Two distinct mechanisms, with a promotion path between them:

1. **Roslyn scripting (`.csx`)** — single-file scripts the agent itself can
   write mid-session, hot-loaded via `Microsoft.CodeAnalysis.CSharp.Scripting`.
   Preserves Pi's "ask the agent to extend itself, /reload, keep going" feel.
2. **NuGet packages** — full-fat extensions with dependencies and types,
   loaded via collectible `AssemblyLoadContext`. The serious-extension tier.

The novel piece: a `promote` command that scaffolds a stable `.csx` into a
proper csproj with the script body as the entry point. Agent writes → user
keeps → it becomes a real package.

### Two distribution profiles

- **JIT self-contained** (~70-100MB) — full runtime extensibility, Roslyn and
  AssemblyLoadContext both available.
- **AOT-trimmed** (~15-30MB) — built-in tools only, no runtime extension
  loading, for users who want a single small CLI binary.

Don't over-invest in AOT until we know which audience matters.

### Microsoft Agent Framework: plumbing, not stack

Use [MAF](https://github.com/microsoft/agent-framework) for the LLM provider
abstraction and tool-calling protocol. Don't assume the agent loop itself
belongs inside MAF's primitives — feel where the friction is first. MAF's
multi-agent orchestration is interesting but not what makes a single-agent
coding tool good.

## What to Steal from Pi

- Session-as-relocatable-directory (cp/share/fork mid-conversation).
- Multi-provider switching with hotkey cycling.
- Explicit `/reload` verb.
- JSON-over-stdio RPC protocol shape (so existing Pi frontends could
  theoretically point at our agent with adapter work).
- Skills/extensions as a first-class concept.

## What to Do Differently

- C#-native idioms throughout (no TypeScript-shaped APIs).
- Two-tier extension model (Pi has one).
- Desktop UI as a first-class host, not an afterthought.
- AOT-trimmed distribution profile for the no-extensions audience.

## Avalonia UI Possibilities (V1.5+, not V1)

Things a desktop UI earns its keep on:

- Visual diff previews before approving file edits.
- Side-by-side tool output panels (file tree, terminal, web preview).
- Session-graph view of forked conversations.
- Skill/extension browser.
- Multi-session tabbed view.

None of these are needed for V1. They're what justifies the Avalonia host's
existence long-term.

## Open Questions for Exploration

- Does MAF's tool-call protocol map cleanly onto the JSON-RPC surface, or
  does it force shape decisions we'd rather make ourselves? Build a tiny
  throwaway to find out.
- What's the right session storage format? Pi uses a directory tree. SQLite?
  JSONL? Filesystem + index?
- How does the agent discover and resolve extensions — a manifest file in
  the session dir, a global config, both?
- What does the RPC surface actually look like? Start by documenting Pi's
  and decide what to keep, change, or extend.
- Provider auth: API keys via env/config, OAuth flows for Claude/ChatGPT
  subscriptions, both? How much of that does MAF give us?
- Sandbox/permission model for tool execution — bash, file writes, network.
  Pi has a model; what's ours?

## Out of Scope for V1

- Multi-agent orchestration.
- The Avalonia host (build the console host first, prove the RPC surface).
- Skill marketplace / discovery service.
- Remote agent execution.
- Mobile hosts.

## References

- [Pi (earendil-works/pi)](https://github.com/earendil-works/pi) — primary inspiration
- [pi-coding-agent (Emacs frontend)](https://github.com/dnouri/pi-coding-agent) — RPC client reference
- [pi_agent_rust](https://github.com/Dicklesworthstone/pi_agent_rust) — Rust port, useful for comparing what changes when leaving Node
- [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
- [Avalonia UI](https://avaloniaui.net)
- [Roslyn Scripting API](https://github.com/dotnet/roslyn/blob/main/docs/wiki/Scripting-API-Samples.md)
