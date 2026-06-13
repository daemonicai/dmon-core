# ADR-020: Agent Definitions as `.md` + `.cs` Pairs

**Date:** 2026-06-13
**Status:** Accepted
**Depends on:** ADR-019 (composition-root hosting).
**Generalises:** ADR-013 (agent profiles) — and **resolves its Open Question A** (per-profile model / toolset).
**Stays within:** ADR-010's deferral line (multi-agent orchestration remains deferred).

## Context

ADR-013 made an *agent profile* a named bundle — persona + `assets` flag + permission mode — selected per session and immutable for that session. Its **Open Question A** explicitly deferred letting a profile also pin a provider/model or an active-extension subset, because under the ADR-009 model that would have meant re-introducing a config-driven extension list per profile.

ADR-019 removes that obstacle: composition is now a file-based program (`Dmon.cs`), and the root `Dmon.cs` is simply the *default* agent. The natural generalisation is that **any named agent is a persona plus a composition root** — and dmon already mirrors the `.claude/agents/*.md` convention (this repo's own OpenSpec workflow ships `worker.md` and `reviewer.md`). The persona half already has a home; the composition half is what ADR-019 newly makes expressible as a file.

## Decision

1. **An agent definition is a `.md` + optional `.cs` pair** under `.dmon/agents/<name>.{md,cs}`. The root `Dmon.cs` (ADR-019) is the **default** agent; named agents live in `.dmon/agents/`.

2. **`.md` declares; `.cs` overrides.** `worker.md` carries the persona (system-prompt block) and declarative frontmatter — model hint, extension/tool list, permission mode, `assets` flag — extending ADR-013's profile config. `worker.cs` is the *sovereign* composition root for that agent (ADR-019's hosting model, scoped to the agent): `#:package` its extensions and builder-wire its model and permission mode. This is the same **markdown-declares / C#-overrides** law as ADR-019 Decision 6.

3. **Resolution rule.** `.cs` present → it is the authoritative composition and the `.md` supplies only persona/prompt. `.cs` absent → the `.md` frontmatter plus root defaults apply, which is exactly an ADR-013 profile with no code. A `.cs` with no matching `.md` uses the default persona.

4. **Per-session selection only.** An agent is chosen at session start — `dmon --agent worker`, or the `profile`/agent parameter on `createSession` (ADR-013 D5 / ADR-012) — and is **immutable for that session** (ADR-013 D1). It compiles once at start (ADR-019 incremental cache). **Mid-flight switching is deferred to v1.x**; when added it is "re-run a different `.cs` → restart core → reattach session," which the ADR-008 restart-between-turns boundary already supports — so it needs no format change here.

5. **Shared composition via `#:include` / `#:ref`.** Agents that share a provider and base toolset factor the common wiring into `.dmon/agents/_common.cs` and pull it in with the .NET 10 file-based-program directives (ADR-019) — DRY across agent definitions without promoting to a project.

6. **This resolves ADR-013 Open Question A.** The `.cs` file *is* how a profile pins model and extension subset. Profiles (ADR-013) become the V1 *selection surface* of agent definitions; the persona + `assets` + permission-mode bundle (ADR-013 D1) is retained and now additionally carries composition code.

7. **The scope line holds.** Per-session selection is **one core at a time** — squarely in scope. A core that *spawns and coordinates other agents' cores* is multi-agent orchestration (multiple `dmon-core` processes over stdio/RPC) and **remains deferred** (ADR-010). The definition format is deliberately designed so that, when ADR-010's deferral lifts, an orchestrator launches the *same* `worker.cs` it can already select — no redefinition.

## Consequences

- **Profiles gain model and toolset** (closes ADR-013 OQ-A) with no new config surface — the capability is the `.cs`, governed by ADR-019, not a new YAML schema.
- **The persona/permission/assets bundle is preserved** (ADR-013 D1, immutable per session) and now extensible with code; the incoherent-combination guard ADR-013 built is unaffected.
- **`worker`/`reviewer` get a native dmon representation** as *definitions*, even though dmon will not *orchestrate* them in V1 (the irony that this repo's worker/reviewer loop runs in Claude Code, not dmon, is precisely because orchestration is deferred).
- **Mid-flight switch costs a restart in v1.x** — established as acceptable and forward-compatible; no format debt incurred now.
- **New surfaces:** a `.dmon/agents/` directory and an `--agent` launch flag / `createSession` selector. No existing message or behaviour is reshaped.

## Alternatives

- **Frontmatter-only profiles (no `.cs`).** That is just ADR-013, and it cannot pin model/toolset without resurrecting a config-driven extension list (ADR-009, now superseded by ADR-019). Rejected.
- **A single `.cs` per agent with the persona in code too.** Rejected: persona is prose and config (ADR-013 keeps it persona-agnostic, replaceable text), markdown is its right home, and folding it into code breaks the markdown-declares law and makes personas un-editable by non-coders.
- **Allow mid-flight switching in V1.** Rejected for V1: conversation history accretes under one persona and one permission posture (ADR-013 D1 / D4 "mutable switching rejected"); deferring keeps that invariant and avoids a mid-session recompile path.

## Open Questions

- **A. Frontmatter ↔ builder precedence detail.** Code wins (Decision 2), but the exact merge for partially-overlapping declarations (e.g. a `.md` tool allow-list alongside `.cs` `AddExtension` calls) needs spelling out.
- **B. Root agent location.** Whether the default stays `./Dmon.cs` (ADR-019 discoverability) or normalises to `.dmon/agents/default.cs` for uniformity with named agents.
- **C. Orchestration forward-compatibility.** Confirm the selection format genuinely doesn't preclude the deferred spawn-based orchestration (ADR-010) when that deferral is revisited.

## Relationship to other ADRs

- **ADR-010** — the deferral line is respected: per-session selection is single-core; orchestration of multiple cores stays deferred, with the format designed to accommodate it later.
- **ADR-012** — `createSession`'s profile/agent selector chooses the definition; the gateway, transport, and resume protocol are unchanged.
- **ADR-013** — generalised, not contradicted: persona + `assets` + permission mode remain the immutable per-session bundle; this ADR resolves Open Question A by letting the bundle carry a `.cs` composition root.
- **ADR-019** — the hosting model this is built on; the root `Dmon.cs` is the default agent, and each `.cs` here is an ADR-019 composition root scoped to one named agent.
