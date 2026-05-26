# dmon — Project Instructions

## What this project is

dmon (pronounced like "demon") is a .NET-native coding agent inspired by [Pi](https://github.com/earendil-works/pi). It is written in **C# on .NET 10**. The agent core runs as a separate process over JSONL/stdio. Two host surfaces are planned: a console/TUI host and an Avalonia desktop host.

See [`coding-agent-brief.md`](./coding-agent-brief.md) for the full vision and architectural rationale.

---

## Tech stack

- **Language:** C# 13 / .NET 10
- **LLM abstraction:** `Microsoft.Extensions.AI` (`IChatClient`) — see ADR-001
- **Extension model:** Two tiers — `.csx` scripts (hot-loaded) and NuGet packages via `AssemblyLoadContext` — see ADR-002
- **RPC protocol:** JSONL over stdio, Pi-compatible shape — see ADR-003
- **Session storage:** Relocatable directory with `messages.jsonl` + `meta.json` + `attachments/` — see ADR-004
- **Provider auth:** API keys via env vars or config file — see ADR-005
- **Permission model:** Tiered prompts (read/write/bash/network), conservative by default — see ADR-006

---

## Architecture Decision Records (ADRs)

ADRs in [`docs/adrs/`](./docs/adrs/) are **binding**. Accepted ADRs must not be contradicted by code or proposals. If new information warrants reconsidering a decision, write a new ADR with status **Supersedes: ADR-NNN** and get it accepted before implementing the change.

Key accepted decisions:

| ADR | Decision |
|-----|----------|
| ADR-001 | Use `IChatClient` (M.E.AI) for LLM abstraction. No MAF dependency. |
| ADR-002 | Extensions expose `AIFunction` via `IDaemonExtension`. No wrapper interface. |
| ADR-003 | JSONL over stdio, Pi-compatible protocol shape. Not strict JSON-RPC 2.0. |
| ADR-004 | Session = relocatable directory. `messages.jsonl` append-only. Large outputs in `attachments/`. |
| ADR-005 | API keys only (env or config). No OAuth for V1. |
| ADR-006 | Conservative permission model. Read within CWD is implicit; all writes require a prompt. |

New ADRs belong in `docs/adrs/ADR-NNN-<slug>.md`. Use the existing ADRs as the format template.

---

## OpenSpec workflow

All planned changes go through the OpenSpec workflow in [`openspec/`](./openspec/).

### Proposing a change

Use `/opsx:propose` to create a new change. This generates a proposal, design, spec, and task list under `openspec/changes/<slug>/`.

### Implementing a change

Use `/opsx:apply` to work through the tasks for a change. Tasks are organised into groups in `openspec/changes/<slug>/tasks.md`. Process **one group at a time**, and for each group:

1. Spawn the `worker` agent to implement the tasks in that group — and only that group. Do not let the worker stray into later groups.
2. Spawn the `reviewer` agent to review the resulting changes.
3. If the reviewer requests changes, send the worker back in to address them. Loop until the reviewer approves.
4. Once approved, `git commit` the changes (Conventional Commits format, scoped to the group) and `git push`.
5. Report progress to the user and ask if they want to continue with the next group. Skip this step if explicitly instructed to "apply all groups" or "apply without pausing" or similar.
6. If user approves or you are running without pausing, move on to the next group of tasks.

Mark tasks completed in `tasks.md` as the worker finishes them.

### Archiving a change

Use `/opsx:archive` once all tasks are done and the code is merged. This moves the change to `openspec/changes/archive/`.

### Rules

- Do not implement features that have no corresponding OpenSpec change unless they are clearly in-scope for an active change.
- Do not leave changes in a partial state — either complete the tasks or document why a task was deferred.
- The `openspec/specs/` directory holds standing specs (interfaces, protocols, schemas). Keep these in sync with the ADRs.

---

## Build and test

> These commands will be added once the project has a buildable solution. Update this section when the first `.sln` is added.

Expected conventions (fill in when ready):

```
dotnet build          # build all projects
dotnet test           # run all tests
dotnet run --project src/Daemon.Console   # run the console host
```

All code must build without warnings. Treat warnings as errors (`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`).

---

## Code style

- Follow standard C# conventions (PascalCase types/members, camelCase locals/params).
- Async methods end in `Async`. Cancellation tokens are always the last parameter and always named `cancellationToken`.
- Prefer `record` for immutable data, `class` for mutable state.
- No `var` when the type is not obvious from the right-hand side.
- No comments that restate what the code does. Only comment non-obvious constraints or workarounds.
- Interfaces are prefixed `I`. No other prefixes or suffixes.
- File-scoped namespaces (`namespace Foo;`) throughout.

---

## Commits

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`.
- Scope is the component: `feat(session):`, `fix(rpc):`, `docs(adr):`, etc.
- Subject line in imperative mood, no period, max 72 characters.
- Reference the OpenSpec change slug in the body if the commit is part of a change: `Change: dmon-core`.

---

## Pi coding agent — specific instructions

The Pi agent (`earendil-works/pi`) uses the skills in `.pi/skills/` and prompts in `.pi/prompts/`. These mirror the Claude Code OpenSpec skills.

### What the Pi agent should do

- Use `/opsx:explore` to think through a problem before proposing.
- Use `/opsx:propose` to create a new OpenSpec change.
- Use `/opsx:apply` to implement tasks from an active change.
- Use `/opsx:archive` to finalise a completed change.

### What the Pi agent must not do

- Must not modify accepted ADRs without writing a superseding ADR first.
- Must not implement features outside an active OpenSpec change (except trivial single-line fixes).
- Must not `git push` or create PRs without explicit user instruction.
- Must not skip build warnings or disable `TreatWarningsAsErrors`.
- Must not take a dependency on Microsoft Agent Framework (MAF) — ADR-001 rules this out.

### Session and RPC

- The Pi agent communicates over JSONL/stdio (ADR-003 shape). Commands use `{"id": "...", "type": "...", ...params}`. Events use `{"id": "...", "event": "...", ...payload}`.
- Do not invent new RPC message types without updating the spec in `openspec/specs/`.

### Provider configuration

- Provider credentials are read from env vars (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`) or a config file. Do not hard-code keys or commit them.

---

## Out of scope for V1

Do not implement, propose, or accept tasks for these unless the brief is explicitly updated:

- Multi-agent orchestration
- Avalonia desktop host
- Skill marketplace / discovery service
- Remote agent execution
- Mobile hosts
- OAuth authentication (noted stretch goal for Gemini/Vertex only)

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:

- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
