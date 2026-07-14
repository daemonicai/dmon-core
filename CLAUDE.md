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
| ADR-009 | *(Superseded by ADR-019.)* Config-driven extension loading (`config.yaml` active-extension list, reflection-loaded at startup) — replaced by composition-root hosting. |
| ADR-010 | A scoped single-turn in-process `IChatClient` in a tool extension is in scope; multi-agent orchestration (multiple `dmon-core` processes over stdio/RPC) remains deferred. |
| ADR-011 | Distribution model: granular contract packages on nuget.org; `dmon` (dotnet tool) acquires `dmoncore` at runtime into the global NuGet cache (no bundling); 3-part protocol-keyed version scheme (`Major.Minor` = wire protocol). |
| ADR-012 | Remote access transport: a WebSocket gateway with connection-decoupled, resumable sessions; **Tailscale** is the auth/encryption boundary (single-tenant, home-server); optional shared key for defense-in-depth. |
| ADR-013 | *(Superseded by ADR-022.)* Agent profiles: a named bundle (persona + per-session `assets/` toggle + permission mode) — dissolved into builder verbs and `.cs` selection. |
| ADR-014 | Gateway event replay uses an in-memory per-session `seq` buffer in the live handler, **not** `messages.jsonl` (which holds only conversational turns, written only by the core). Amends ADR-012 Decision 4. |
| ADR-015 | Command results are dedicated typed events correlated by command `id` (`ResultEvent` base; `CommandErrorEvent` for failures); the generic `{type:"response", data}` envelope is retired. Makes the wire contract describable for client generation. Amends ADR-003's response framing. |
| ADR-016 | Conversation persistence: session-storage owns a lossless, dmon-owned **parts** record (memory tiers derive their index from it); no third-party types in the API definition; lenient mapping preserves unmodelled content as render-only opaque `UnknownPart`. Amends ADR-004/ADR-014; supplies ADR-015's deferred `getMessages` DTO. |
| ADR-018 | Gateway auth: a per-device, revocable key set (file-backed `devices.json`, hot-reloaded, fail-closed to last-good) replaces the single shared key; a match tags the connection with its `keyId` and revocation fences live connections. Amends ADR-012 Decisions 6/10/12. |
| ADR-019 | Composition-root hosting: `dmoncore` is a **library**; a .NET 10 file-based program `Dmon.cs` (built, then run with `--no-build` so the build phase stays off the stdio wire) is the core entry point and declares its extensions as compile-time `#:package` deps. Supersedes ADR-009 in full, ADR-011 D2–4, ADR-008's dynamic-load mechanism, and ADR-002's `.csx`/`promote` tier. |
| ADR-020 | *(`.md`-persona/bundle framing superseded by ADR-022.)* Agent definitions under `.dmon/agents/`; the root `Dmon.cs` is the default agent. Per-session selection, single-core, and gateway-workspace-root resolution are **retained** (now keyed to `.cs` composition roots, not `.md`+`.cs` pairs). |
| ADR-021 | Apex `compose` permission tier: an **agent-initiated** reload that changes the composition root is gated (new packages approved by exact pin), never globally suppressible, and parks (never auto-approves) when headless. Amends ADR-006 (replaces its extension-loading gate); closes ADR-019 Open Question D. |
| ADR-022 | Composition root as a feature: open **registration facets** (`IProviderRegistration`/`IToolRegistration`/`IMiddlewareRegistration`) + thin `IDmonHostBuilder` over `Services`/`Configuration`, all verbs as extension methods (`Use`/`Add`/`With`/`Append`); DI-discovery for tools/middleware/providers; **Option-B** provider symmetry; sub-agent isolation via `Action<IProviderRegistration>` → `IChatClientFactory`; `IDmonExtension`→`IToolExtension`; **`Dmon.Extensions` deleted — all author-facing contracts collapse into `Dmon.Abstractions`**. **Supersedes ADR-013 in full and ADR-020's persona/bundle framing** — an agent *is* its `.cs`; no profiles/personas/`.md`. Amends ADR-019/002/010/006/021/012. |
| ADR-023 | Extension package topology: `dmoncore` becomes a **vendor-SDK-free engine**; every provider/tool/middleware ships as its **own granular implementation package** (`Dmon.Providers.<Name>`, `Dmon.Tools.<Name>`, …) carrying its SDK + fluent verb (in the `Dmon.Hosting` namespace); first-party packages + contracts version **lockstep on the protocol line** (incompat fails at restore); builtin tools scaffolded-but-removable; sub-agent tools provider-agnostic by default. Extends ADR-011 D1/D5/D6; builds on ADR-019/022. |
| ADR-024 | Protocol-cycle release versioning: `X.Y` = wire protocol `Major.Minor`, **lockstep + protocol-keyed across the whole first-party set**; `Z` = **patch, independent per package** within a cycle. A protocol `Major`/`Minor` bump re-releases **everything** at `X.Y.0` (the cycle marker); per-package **breaking changes are bound to cycle boundaries**. Retains the universal `@X.Y.*` one-pin + restore-time-incompat guarantees (no bundle package needed); frontends add a runtime `agentReady` compat gate. **Amends ADR-011 D5 and ADR-023 D5** (frees patch from lockstep). Versioning prerequisite for the forthcoming monorepo change. |
| ADR-025 | Monorepo consolidation: fold the first-party `dmon-*` .NET repos into one repo with top-level buckets `core/ providers/ tools/ middleware/ frontends/` (+ `samples/`, `libs/`), each with its own `.slnx` plus a root `Everything.slnx`; **intra-repo `ProjectReference`** (PackageRef only for external); **hybrid openspec roots** (root = system/cross-cutting, per-area = component-local) under one boundary rule that also governs ADR placement; **git-worktree-per-area** parallel component work (cross-cutting on `main`); nested `Directory.*.props`; dependency-aware **path-filtered CI** ("core ⇒ all"); two-family **release matrix** (NuGet vs app artifacts) on ADR-024's per-package triggers. Builds on ADR-019/022/023/024. Open: import mechanics, dcli/swift placement. |
| ADR-026 | Memory is its **own top-level bucket `memory/`**, not middleware: it has the **contract-set + N backends + facade** shape (`core/Dmon.Abstractions.Memory` + `memory/Dmon.Memory` sqlite-vec short-term + `memory/Dmon.Memory.Meko` long-term) like `providers/`/`tools/`, and implements **none** of ADR-023's `IDmonMiddleware` chat-pipeline role (`AddDmonMemory()` registers DI services; `IMemory` is consumed by `SessionStore` in core, never as a pipeline stage). Contracts stay in `core/`; `middleware/` is **retained as a named ADR-023 role with no current members** (created when the first real middleware ships); `dmon-meko` grafts to `memory/Dmon.Memory.Meko` (no type rename). **Amends ADR-025 D2 (bucket set), D5/6 (`memory` openspec path), D11 (`dmon-meko` landing target).** |
| ADR-027 | Two general composition seams in `core/`: **`ITerminalClientFactory`** (single-impl; when registered, its output replaces the provider-registry active provider as the terminal `IChatClient` in `Build()` — the no-factory path is unchanged) and **`IAbilityProvider`/`AbilityRegistry`** (per-turn scope-gated tool manifest keyed on an **opaque `string` scope** — no `Personal`/`World` enum in core; orthogonal to `IToolExtension`; `AddAbilities<T>()` on `IToolRegistration`). A multi-backend terminal client is **not** middleware (it doesn't `Wrap` one inner client); routing **policy** (the Daemon's `TriageRouter`, scope vocabulary, `UseTriage`/`AddReasoner`/`AddEgress`) lives in `daemon/Daemon.Routing`, **not** `middleware/` and **not** a protocol-lockstep first-party package — `middleware/` stays empty (upholds ADR-026 D4). Egress is explicit provider-agnostic `AddEgress(IChatClient)`. **Amends ADR-019 (`Build()` terminal-client selection); extends ADR-022; honours ADR-023/026.** |
| ADR-028 | Personal-assistant monorepo topology: a **`daemon/` bucket** (Daemon *composition* — `Daemon.cs` + `Daemon.Routing` + `Daemon.App`/dmonium menu-bar app) and a **new `services/` bucket** for standalone backing **server** apps that pair with a `tools/` extension (`services/Dcal` moved from `daemon/Daemon.Calendar`; `services/Dmail` is the future home of the Dmail server). Servers are **app artifacts, independently versioned — not** NuGet/protocol-keyed. `dmonium` lands in `daemon/Daemon.App` (not `frontends/`, which stays protocol-surface hosts). The calendar capability is **renamed `dcal`** across server (`services/Dcal`), tool (`tools/Dmon.Tools.Dcal`), and specs (`dcal-lookup`/`dcal-sync`), matching the shipped `DCAL_*` config. **Swift** is a supported in-repo language built outside `Everything.slnx` via `make daemon-app`. **Amends ADR-025 D2/D10/D11; resolves ADR-025 Open Question B (dmonium + Swift + Dmail-server home); honours ADR-023/024/026/027.** |
| ADR-032 | Handler-initiated escalation: routing policy becomes first-line (e4b) → `think_harder` (`FunctionInvocationContext.Terminate`) → escalation (26b, continues with inherited messages), replacing upfront tier-dispatch; drop the reasoner, `Tier` enum, `ReasonerClient`; `AddReasoner`→`AddEscalation`; backend verbs (`UseTriage`/`AddEscalation`/`AddEgress`) take lazy `Func<IServiceProvider, ValueTask<IChatClient>>` resolved on first turn, cached per-backend via `Lazy<Task<IChatClient>>`. **Amends ADR-027 D5 (routing-policy shape); closes ADR-027 OQ-A (lazy-in-router — `Create` stays sync, D1–D4 unchanged). Builds on ADR-007.** |
| ADR-033 | Rename the WebSocket remote-access host `Dmon.Gateway`→`Dmon.Network`; dotnet-tool command `ndmon` (`dotnet tool install -g Dmon.Network`); runtime config section/on-disk store renamed (`~/.dmon/network`, `[dmon-network]`, `DMON_NETWORK_PATH`) as a clean break; the **wire/contract strings stay** (`gw` discriminator, `Dmon.Protocol.Gateway`, control frames) — renaming them would need a protocol bump. **Amends ADR-012/017/018/028 (host-name prose only); builds on ADR-024.** |
| ADR-034 | MLX local runtime: replace oMLX with `Dmon.Providers.Mlx` (uv venv pins `mlx_lm`, two keyed fixed-port runtimes firstline:8800/escalation:8810, attach-first `EnsureRunningAsync`/`StopAsync`, completion-readiness not `/v1/models`, reasoning field dropped) + core `ISessionActivityListener` seam + daemon `EscalationWarmingService` (provider-agnostic, TimeProvider idle timer, fire-and-forget); `Dmon.Providers.Omlx` deleted. **Amends ADR-006/007/032.** |
| ADR-035 | Release matrix — resolve the protocol-cycle versioning Open Questions: patch `Z` from **explicit per-package tags** `<area>/<name>-vX.Y.Z` via MinVer (not commit-height); a cycle boundary **re-releases the whole set at `X.Y.0`** (no-op releases are real, required by `@X.Y.*` restore); **two release families keyed by publish sink** — NuGet (nuget.org, protocol-lockstep) vs app-artifact (GitHub Release bundle, runtime `agentReady` enforcement); **`ndmon` is NuGet-tool family** (narrows ADR-033 D2 to `Z`-only independence); **`Dmon.Memory` made packable**; one **area→paths map** shared by path-filtered CI and releases (`core/`⇒all). Provides the package→family→tag table the `release-matrix` change implements. **Amends ADR-024 (OQ-A/B/C, D8), ADR-025 (OQ-E, D10), ADR-023 (Dmon.Memory); narrows ADR-033 D2. Builds on ADR-011/026.** |

New ADRs belong in `docs/adrs/ADR-NNN-<slug>.md`. Use the existing ADRs as the format template.

---

## OpenSpec workflow

All planned changes go through the OpenSpec workflow in [`openspec/`](./openspec/).

### Proposing a change

Use `/opsx:propose` to create a new change. This generates a proposal, design, spec, and task list under `openspec/changes/<slug>/`.

### Implementing a change

Use `/opsx:apply`. **This subsection is authoritative** — if the skill's behaviour ever conflicts with what's written here, follow this document.

#### Roles — the main thread never plans or writes feature code

- **Orchestrator** = the main thread (you). You drive the loop: run pre-flight, spawn agents, run the gates, tick boxes, keep `DEVLOG.md` current, handle the user conversation, and commit. **You do not plan the blocks, write the briefs, or implement feature code directly** — those belong to the architect and worker.
- **`architect`** agent (Opus) — looks at the remaining tasks and picks the **smallest reasonable, independently gate-passing block** of work (one task or a small contiguous range), then writes the self-contained brief for the worker. Plans and briefs only; never edits, spawns, or commits. Flags stop-and-ask blockers (including any task that would contradict a binding ADR) instead of briefing around them.
- **`worker`** agent (Sonnet) — implements the block from the architect's brief; writes tests; leaves the tree green.
- **`reviewer`** agent (Opus) — audits the worker's block diff and **reports findings; it does not edit code.**

All three agents are defined in `.claude/agents/`. Delegate; don't shortcut by planning, briefing, or implementing yourself.

#### Pre-flight (orchestrator, once before the first block)

1. Skim `proposal.md` and `design.md` for context (the **architect** reads them — and the specs and ADRs — in depth when it plans each block).
2. **Working tree must be clean** (`git status`). If dirty, stop and ask.
3. **Change must validate:** `openspec validate <slug> --strict`. If not, stop and ask.
4. **Be on the change branch** `change/<slug>`. Create it from `main` if missing: `git switch -c change/<slug>`.

#### Implement — architect-planned blocks

The unit of work is a **block**: the **smallest reasonable, independently gate-passing** slice of remaining tasks — one task (e.g. `1.3`) or a small contiguous range (e.g. `1.3`–`1.5`). The **architect** chooses each block; you don't pick it yourself. Loop until every task in the change is ticked:

1. **Plan the block (architect).** Spawn the `architect`. It reads `tasks.md` (ticked state), `proposal.md`, `design.md`, the relevant specs, the binding ADRs, and `DEVLOG.md`, then returns **(a)** the block's task ids + deliverable name and **(b)** a self-contained worker brief (tasks, binding spec/ADR excerpts, design decisions, already-resolved `DEVLOG` decisions, scope boundaries, investigation pointers, contract/permission/persistence hazards, gates).
   - The architect is spawned **fresh each block** — so the **`DEVLOG.md` is its cross-block memory**. Keep the DEVLOG current (step 7) or the architect plans blind. (Maintain `DEVLOG.md` via the devlog skill.)
   - **If the architect flags a BLOCKER** (ambiguity, contradiction, unresolved Open Question, out-of-scope need, spec-looks-wrong, or a task that would contradict a binding ADR), go to *Stop and ask* — don't brief around it.
2. **Brief the worker.** Hand the architect's brief to the `worker` verbatim (add the gates below if the architect didn't). The worker implements the **whole block** — splittable across multiple `worker` calls if needed, but it remains **one commit** at block end.
3. **Audit.** Spawn `reviewer` on the **block diff** (correctness, ADR compliance, OpenSpec scope, C# idiom, agentic-AI design quality, security).
4. **Review loop.** Feed the reviewer's findings to the `worker`; worker fixes; `reviewer` re-audits. **Repeat until the reviewer signs off.** (Doc-only spec/design realignments and the `DEVLOG.md` are the orchestrator's to edit — agents don't.)
5. **Gates — all must pass before ticking any box:**
   - `make build` clean (no errors; `TreatWarningsAsErrors` clean)
   - `make test` (or `env -u MEKO_API_KEY make test` to avoid the live-Meko smoke hang) green — new tests for the block **and** all existing tests
   - `openspec validate <slug> --strict`

   If a gate fails, it's back to step 4, not a commit.
6. **Tick the boxes.** Mark every `- [x] N.M` in the block in `tasks.md`. Never rewrite `tasks.md` wholesale — only flip `[ ]→[x]`.
7. **Update the DEVLOG.** Record the block's decisions/deviations so the next (fresh) architect can plan.
8. **Commit — one conventional commit per block**, scoped to the component, with the change slug in the body (`Change: <slug>`). Use the real task ids the block covered. Then loop back to step 1 for the next block.

#### Stop and ask — do not improvise

Stop **immediately** and ask the user (do not improvise a fix) — whether surfaced by the **architect** while planning, the **worker** mid-implementation, or the **reviewer** — when: a spec/design is **ambiguous** or two specs **contradict**; doing the task properly needs changes **outside this change's scope**; a task is **blocked by an unresolved Open Question** in `design.md`; implementation reveals the **spec itself is wrong**; a task would require **contradicting a binding ADR** (the path is a superseding ADR the user must accept first); or a task **requires human-in-the-loop verification** automated gates can't settle (give a precise, copy-pasteable verification recipe and wait for confirmation before ticking it).

**On stopping mid-block:** leave the WIP **uncommitted**, do **not** tick the block, do **not** revert. Report the **exact task (`N.M`)** that stopped you and why.

#### Done

When every task is ticked and the final review is clean: report blocks completed, commits made, and the test summary; push the `change/<slug>` branch (and open a PR) when the user asks; then **propose `/opsx:archive`** and **wait for confirmation**. Do not archive automatically.

### Archiving a change

Use `/opsx:archive` once all tasks are done and the code is merged. This moves the change to `openspec/changes/archive/`.

### Rules

- Do not implement features that have no corresponding OpenSpec change unless they are clearly in-scope for an active change.
- Do not leave changes in a partial state — either complete the tasks or document why a task was deferred.
- The `openspec/specs/` directory holds standing specs (interfaces, protocols, schemas). Keep these in sync with the ADRs.

---

## Build and test

The solution is `Everything.slnx`; common tasks are wrapped in the `Makefile`.

```
make build            # publish core, terminal, and extensions into build/
make test             # dotnet test -c Release (all test projects)
make clean            # remove build/

dotnet build Everything.slnx -c Release       # quick whole-solution compile check
dotnet test -c Release                        # run all tests
dotnet run --project frontends/Dmon.Terminal  # run the terminal host (spawns Dmon.Core)
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

## serena

Use the **serena** MCP server where appropriate for operations on code in this repository. Serena provides language-server-backed, symbol-aware tooling that is more precise and token-efficient than raw file reads or text search for most code work.

Prefer serena's tools when:

- **Navigating code** — `find_symbol`, `get_symbols_overview`, `find_declaration`, and `find_implementations` to locate types/members without reading whole files.
- **Tracing usage** — `find_referencing_symbols` to find callers and dependents before changing a symbol.
- **Editing by symbol** — `replace_symbol_body`, `insert_before_symbol`, `insert_after_symbol`, and `rename_symbol` for structural edits that respect C# syntax.
- **Checking correctness** — `get_diagnostics_for_file` after edits.

Call `initial_instructions` before starting a serena-driven coding task. Plain `Read`/`Edit` remain fine for small, well-located changes and non-code files.
