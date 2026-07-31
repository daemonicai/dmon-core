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

Summaries of all 35 accepted ADRs — what each decided, and what it amends or
supersedes — are in the **`adr-index` skill** (`.claude/skills/adr-index/`).
Load it when you need to know what a given ADR decided or which ADR governs a
subsystem. The ADR files themselves remain the source of truth.

New ADRs belong in `docs/adrs/ADR-NNN-<slug>.md`. Use the existing ADRs as the format template.

---

## OpenSpec workflow

<!-- dmons-scaffold: 0.3.0 -->

All planned changes go through the OpenSpec workflow in [`openspec/`](./openspec/).

- **Propose** — `/opsx:propose` creates a change: proposal, design, spec, and task list under `openspec/changes/<slug>/`.
- **Implement** — `/opsx:apply`. The block-by-block mechanics (DEVLOG conventions, pre-flight, carving blocks, the worker/reviewer loop, the gates, the per-section supervisor review, done criteria) are in the **`openspec-apply` skill** (`.claude/skills/openspec-apply/`). Load it before applying a change. The roles and prohibitions below always apply and take precedence over it.
- **Archive** — `/opsx:archive` once all tasks are done and the code is merged. Moves the change to `openspec/changes/archive/`.

### Roles — the Product Owner owns the vision; the main thread never writes feature code

- **Product Owner** = the user. They hold the vision. Every *product* call — what to build, which change to apply, how to resolve an ambiguity or a wrong spec — is theirs. You realise their vision; you do not decide it for them.
- **Analyst/Architect** = the main thread (you), on **Opus**. One role, two hats — and you should know which you're wearing:
  - **Analyst** during `/opsx:explore` — shaping *what* with the Product Owner.
  - **Architect** during `/opsx:propose` and the whole apply loop — you shape *how*, then orchestrate the build: read the specs and ADRs, carve each section into blocks, write the briefs, spawn the agents, run the gates, tick boxes, keep `DEVLOG.md` current, and commit. **You do not implement feature code directly.**
- **`worker`** agent (Sonnet) — implements each block from your brief; writes tests; leaves the tree green.
- **`reviewer`** agent (Sonnet) — audits each block's diff and **reports findings; it does not edit code.** One reviewer for the whole change.
- **`supervisor`** agent (Opus) — audits each finished `## N.` section as a whole, once all its blocks have landed. One supervisor for the whole change.

**The two auditors have different jobs and must not be swapped.** The `reviewer` is **diff-local** and runs per block; the `supervisor` is the only agent that ever sees more than one block at a time, and looks for what block reviews structurally cannot catch — cross-block drift, duplicated abstractions, dead scaffolding, and whether the section genuinely satisfies its spec rather than merely ticking its tasks. Neither ever edits code: both report, and a worker fixes.

The agents are defined in `.claude/agents/`. Delegate; don't shortcut by implementing yourself.

### Stop and ask — do not improvise

These are the **Product Owner's** calls, not yours. Stop **immediately** and ask (do not improvise a fix) — whether you hit it while carving a block, or the **worker** hit it mid-implementation, or the **reviewer** or **supervisor** surfaced it — when: a spec/design is **ambiguous** or two specs **contradict**; doing the task properly needs changes **outside this change's scope**; a task is **blocked by an unresolved Open Question** in `design.md`; implementation reveals the **spec itself is wrong**; a task would require **contradicting a binding ADR** (the path is a superseding ADR the user must accept first); a task **requires human-in-the-loop verification** automated gates can't settle (give a precise, copy-pasteable verification recipe and wait for confirmation before ticking it); or the **supervisor still requests changes after one remediation block** — report its findings and ask whether to remediate again, re-cut the section, or fix the spec.

**On stopping mid-block:** leave the WIP **uncommitted**, do **not** tick the block, do **not** revert. Report the **exact task (`N.M`)** that stopped you and why.

### Rules

- Do not implement features that have no corresponding OpenSpec change unless they are clearly in-scope for an active change.
- Do not leave changes in a partial state — either complete the tasks or document why a task was deferred.
- The `openspec/specs/` directory holds standing specs (interfaces, protocols, schemas). Keep these in sync with the ADRs.

---

## Build and test

The solution is `Everything.slnx`; common tasks are wrapped in the `Makefile` (`make build`, `make test`, `make clean` — run `make help` or read the `Makefile` for the rest).

- `make test` hangs for ~90s on the live-Meko smoke test when `MEKO_API_KEY` is set — use `env -u MEKO_API_KEY make test`.
- `dotnet run --project frontends/Dmon.Terminal` runs the terminal host (spawns Dmon.Core).
- `openspec validate <slug> --strict` validates an OpenSpec change.

All code must build without warnings — `TreatWarningsAsErrors` is on. Do not suppress warnings or disable analyzers to make the build pass.

---

## Code style

- Async methods end in `Async`. Cancellation tokens are always the last parameter and always named `cancellationToken`.
- Prefer `record` for immutable data, `class` for mutable state.
- No `var` when the type is not obvious from the right-hand side.
- No comments that restate what the code does. Only comment non-obvious constraints or workarounds.

---

## Commits

- Use [Conventional Commits](https://www.conventionalcommits.org/): `feat:`, `fix:`, `chore:`, `docs:`, `refactor:`, `test:`.
- Scope is the component: `feat(session):`, `fix(rpc):`, `docs(adr):`, etc.
- Subject line in imperative mood, no period, max 72 characters.
- Reference the OpenSpec change slug in the body if the commit is part of a change: `Change: dmon-core`.

---

## Pi coding agent

Pi-specific instructions live in [`.pi/AGENT.md`](./.pi/AGENT.md). Everything in this file applies to Pi too.

---

## Out of scope for V1

Do not implement, propose, or accept tasks for these unless the brief is explicitly updated:

- Multi-agent orchestration
- ~~Avalonia desktop host~~ — **now in scope** (gating precondition met): the single-session `frontends/Dmon.Desktop` host ships. Multi-session / multi-core tabbing and the V1.5+ desktop affordances remain deferred. See the scope clarification below.
- Skill marketplace / discovery service
- Generic multi-user / public remote agent execution (a single-tenant Tailscale-fronted gateway is in scope — ADR-012)
- Mobile hosts as first-class agent hosts (a personal iOS client of the ADR-012 gateway is in scope)
- OAuth authentication (noted stretch goal for Gemini/Vertex only)

**Scope clarification (ADR-010):** "Multi-agent orchestration" means multiple `dmon-core` **processes** communicating over the stdio/RPC interface. A tool extension that constructs a scoped, single-turn in-process `IChatClient` to fulfil a tool call is *in scope* — it is simply an extension using an additional LLM model, not orchestration. See [`docs/adrs/ADR-010-sub-agent-extensions.md`](./docs/adrs/ADR-010-sub-agent-extensions.md).

**Scope clarification (ADR-012/013):** A *single-tenant* remote access gateway — one user's `dmoncore` sessions exposed over WebSocket to a personal iOS client, reached only over **Tailscale** — is **in scope**. This is not "remote agent execution" in the deferred sense (multi-user / public / untrusted); it is one user reaching their own home-server agent over a private overlay. Selectable **agents** — each its own `.cs` composition root under `.dmon/agents/`, carrying its system prompt, permission mode, and assets as builder verbs (ADR-022, superseding the ADR-013 profile bundle) — are likewise in scope. See [`docs/adrs/ADR-012-remote-session-transport.md`](./docs/adrs/ADR-012-remote-session-transport.md) and [`docs/adrs/ADR-022-composition-root-registration-facets.md`](./docs/adrs/ADR-022-composition-root-registration-facets.md).

**Scope clarification (Avalonia desktop host):** The brief's gating precondition — *"build the console host first, prove the RPC surface"* — is **met**: `Dmon.Terminal` ships and the host-facing RPC surface (`IRpcTransport`/`IRpcClient`/`ICoreLauncher`/`ICoreProcess`) lives in `Dmon.Runtime`, consumed by Terminal and Gateway. The Avalonia host (`frontends/Dmon.Desktop`) is therefore **in scope** as a thin local-spawn frontend over `Dmon.Runtime` at single-session parity with the TUI (ReactiveUI MVVM with routing from the start; PipBoy theme; `Markdown.Avalonia`). **Still deferred:** multi-session / multi-core tabbing (free at the per-instance runtime layer, an additive future change) and the V1.5+ affordances in the brief (visual diff preview, side-by-side tool panels, session-graph view, extension browser). A self-contained installable artifact that bundles its own core is also deferred — the first cut resolves the core at runtime from the NuGet cache like the `dmon` tool. No new ADR (all decisions fall inside existing ADRs).

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:

- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

