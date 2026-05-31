# dmon — Project Instructions

## What this project is

dmon (pronounced like "demon") is a .NET-native coding agent inspired by [Pi](https://github.com/earendil-works/pi). It is written in **C# on .NET 10**. The agent core runs as a separate process over JSONL/stdio. Two host surfaces are planned: a console/TUI host and an Avalonia desktop host.

See [`coding-agent-brief.md`](./coding-agent-brief.md) for the full vision and architectural rationale.

---

## Tech stack

- **Language:** C# 13 / .NET 10
- **LLM abstraction:** `Microsoft.Extensions.AI` (`IChatClient`) — see ADR-001
- **Extension model:** Two tiers — `.csx` scripts (hot-loaded) and NuGet packages loaded into the Default `AssemblyLoadContext` — see ADR-002, ADR-008
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
| ADR-002 | Extensions expose `AIFunction` via `IDmonExtension`. No wrapper interface. (Loading mechanism superseded by ADR-008.) |
| ADR-003 | JSONL over stdio, Pi-compatible protocol shape. Not strict JSON-RPC 2.0. |
| ADR-004 | Session = relocatable directory. `messages.jsonl` append-only. Large outputs in `attachments/`. |
| ADR-005 | API keys only (env or config). No OAuth for V1. |
| ADR-006 | Conservative permission model. Read within CWD is implicit; all writes require a prompt. |
| ADR-007 | Provider-extension lifecycle: `IsApplicable()` at load, `EnsureRunningAsync()` gated, per-runner `IProviderFactory`. |
| ADR-008 | Extensions load into the **Default** `AssemblyLoadContext` (no collectible per-load contexts); reclaim via process restart. Supersedes ADR-002's loading mechanism. |
| ADR-009 | Active extensions are declared in `config.yaml` (user + project), auto-loaded at startup; `/reload` restarts the core. |
| ADR-010 | A scoped single-turn in-process `IChatClient` in a tool extension is in scope; multi-agent orchestration (multiple `dmon-core` processes over stdio/RPC) remains deferred. |
| ADR-011 | Distribution model: granular contract packages on nuget.org; `dmon` (dotnet tool) acquires `dmoncore` at runtime into the global NuGet cache (no bundling); 3-part protocol-keyed version scheme (`Major.Minor` = wire protocol). |
| ADR-012 | Remote access transport: a WebSocket gateway with connection-decoupled, resumable sessions; **Tailscale** is the auth/encryption boundary (single-tenant, home-server); optional shared key for defense-in-depth. |
| ADR-013 | Agent profiles: a named bundle (persona + per-session `assets/` toggle + permission mode) selected per session; built-in `coding` profile preserves today's behaviour; non-coding personas are config. |

New ADRs belong in `docs/adrs/ADR-NNN-<slug>.md`. Use the existing ADRs as the format template.

---

## OpenSpec workflow

All planned changes go through the OpenSpec workflow in [`openspec/`](./openspec/).

### Proposing a change

Use `/opsx:propose` to create a new change. This generates a proposal, design, spec, and task list under `openspec/changes/<slug>/`.

### Implementing a change

Use `/opsx:apply`. **This subsection is authoritative** — if the skill's behaviour ever conflicts with what's written here, follow this document.

#### Roles — the main thread never writes feature code

- **Orchestrator** = the main thread (you). Reads specs, selects work, briefs agents, runs the gates, ticks boxes, and commits. **Does not implement feature code directly.**
- **`worker`** agent — implements the tasks of one group.
- **`reviewer`** agent — audits the worker's diff and **reports findings; it does not edit code.**

Both agents are defined in `.claude/agents/`. Delegate; don't shortcut by implementing yourself.

#### Pre-flight (before any group)

1. Read `proposal.md`, `design.md`, the relevant `specs/<capability>/spec.md`, and any ADRs the change touches.
2. **Working tree must be clean** (`git status`). If dirty, stop and ask.
3. **Change must validate:** `openspec validate <slug> --strict`. If not, stop and ask.
4. **Be on the change branch** `change/<slug>`. Create it from `main` if missing: `git switch -c change/<slug>`.

#### Per group — the unit of work is one `## N.` group in `tasks.md`

Walk groups in order from the first unticked `- [ ]` task. For each:

1. **Brief the worker.** Hand it that group's tasks (`N.1`…`N.k`), the relevant spec excerpts, the binding design decisions / ADRs, and the gates below. Only that group — do not let it stray into later groups.
2. **Worker implements the whole group.** Large groups may span multiple `worker` calls but remain **one commit**.
3. **Audit.** Spawn `reviewer` on the group's diff (correctness, ADR compliance, OpenSpec scope, C# idiom, agentic-AI design quality, security).
4. **Review loop.** Feed the reviewer's findings to the `worker`; worker fixes; `reviewer` re-audits. **Repeat until the reviewer signs off.**
5. **Gates — all must pass before ticking any box:**
   - `make build` clean (no errors; `TreatWarningsAsErrors` clean)
   - `make test` (or `dotnet test -c Release`) green — new tests for the group **and** all existing tests
   - `openspec validate <slug> --strict`

   If a gate fails, it's back to step 4, not a commit.
6. **Tick the boxes.** Mark every `- [x] N.M` for the group in `tasks.md`. Never rewrite `tasks.md` wholesale — only flip `[ ]→[x]`.
7. **Commit — one conventional commit per group**, scoped to the component, with the change slug in the body (`Change: <slug>`).
8. **Report and pause.** Tell the user what landed and ask before the next group — unless told to "apply all groups" / "apply without pausing".

#### Stop and ask — do not improvise

Stop immediately and ask the user when: a spec/design is **ambiguous** or two specs **contradict**; doing the task properly needs changes **outside this change's scope**; a task is **blocked by an unresolved Open Question** in `design.md`; implementation reveals the **spec itself is wrong**; or a task **requires human-in-the-loop verification** automated gates can't settle (give a precise, copy-pasteable verification recipe and wait for confirmation before ticking it).

**On stopping mid-group:** leave the WIP **uncommitted**, do **not** tick the group, do **not** revert. Report the **exact task (`N.M`)** that stopped you and why.

#### Done

When every task is ticked and the final review is clean: report groups completed, commits made, and the test summary; push the `change/<slug>` branch (and open a PR) when the user asks; then **propose `/opsx:archive`** and **wait for confirmation**. Do not archive automatically.

### Archiving a change

Use `/opsx:archive` once all tasks are done and the code is merged. This moves the change to `openspec/changes/archive/`.

### Rules

- Do not implement features that have no corresponding OpenSpec change unless they are clearly in-scope for an active change.
- Do not leave changes in a partial state — either complete the tasks or document why a task was deferred.
- The `openspec/specs/` directory holds standing specs (interfaces, protocols, schemas). Keep these in sync with the ADRs.

---

## Build and test

The solution is `Dmon.slnx`; common tasks are wrapped in the `Makefile`.

```
make build            # publish core, terminal, and extensions into build/
make test             # dotnet test -c Release (all test projects)
make clean            # remove build/

dotnet build Dmon.slnx -c Release          # quick whole-solution compile check
dotnet test -c Release                     # run all tests
dotnet run --project src/Dmon.Terminal     # run the terminal host (spawns Dmon.Core)
openspec validate <slug> --strict          # validate an OpenSpec change
```

All code must build without warnings — `TreatWarningsAsErrors` is on. Do not suppress warnings or disable analyzers to make the build pass.

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
- Generic multi-user / public remote agent execution (a single-tenant Tailscale-fronted gateway is in scope — ADR-012)
- Mobile hosts as first-class agent hosts (a personal iOS client of the ADR-012 gateway is in scope)
- OAuth authentication (noted stretch goal for Gemini/Vertex only)

**Scope clarification (ADR-010):** "Multi-agent orchestration" means multiple `dmon-core` **processes** communicating over the stdio/RPC interface. A tool extension that constructs a scoped, single-turn in-process `IChatClient` to fulfil a tool call is *in scope* — it is simply an extension using an additional LLM model, not orchestration. See [`docs/adrs/ADR-010-sub-agent-extensions.md`](./docs/adrs/ADR-010-sub-agent-extensions.md).

**Scope clarification (ADR-012/013):** A *single-tenant* remote access gateway — one user's `dmoncore` sessions exposed over WebSocket to a personal iOS client, reached only over **Tailscale** — is **in scope**. This is not "remote agent execution" in the deferred sense (multi-user / public / untrusted); it is one user reaching their own home-server agent over a private overlay. Selectable **agent profiles** (ADR-013) — persona + per-session asset directory + permission mode — are likewise in scope. See [`docs/adrs/ADR-012-remote-session-transport.md`](./docs/adrs/ADR-012-remote-session-transport.md) and [`docs/adrs/ADR-013-agent-profiles.md`](./docs/adrs/ADR-013-agent-profiles.md).

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:

- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
