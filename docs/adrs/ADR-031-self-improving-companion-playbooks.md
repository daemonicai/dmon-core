# ADR-031: Self-Improving Companion Playbooks — Router-Matched, Synthesis-Edited Procedural Skills

**Date:** 2026-06-20
**Status:** Proposed
**Amends:** ADR-027 (`RouteDecision` and `ClassifyAndRouteAsync` gain a playbook dimension; the router injects a matched playbook body per turn, parallel to its scoped-tool injection; a new `ITriageEventSink` persists routing + outcome metadata the router currently discards); ADR-022/ADR-020 (a new auto-discovered `.dmon/` asset kind — `.dmon/playbooks/*.md` — and a `PlaybookRegistry` parallel to `AbilityRegistry`)
**Builds on:** ADR-030 (cross-session recall is the synthesis *selection substrate*, joined by playbook-id), ADR-010 (the synthesis distiller is a scoped single-turn in-process sub-agent), ADR-028 (`dmonium` hosts the overnight synthesis; everything lands in `daemon/`), ADR-019 (file-based assets)
**Independent of:** ADR-026 / Meko — playbooks touch **no memory backend**. The capability is store-agnostic by construction.

## Context

Brain's actual thesis (Perplexity, Jun 2026) is memory about *how the agent works* — what worked, what failed, what got corrected — not memory about the user's world. Two grounding facts shape how dmon should realize that:

1. **The memory read path is dead.** `IMemory.SearchAsync` is invoked on the *write* path (indexing) and consumed by *nothing* on the read path; no recalled memory reaches a turn today (ADR-026 Open Question A). A "self-improving memory" that distils facts into a store would therefore write to a black hole.
2. **dmon already improves agents with auto-loaded procedural docs** — `CLAUDE.md` (project procedure), `.dmon/agents/*.cs` (the agent *is* its composition root, ADR-022), the `DEVLOG` (the architect's cross-block procedural memory). The apply-workflow is itself a playbook for "implement an OpenSpec block."

So the self-improving layer is modelled not as fact-consolidation into a store, but as **playbooks**: task-keyed, **skill-shaped procedural documents** that the agent *maintains itself*. They are like Claude Code skills — frontmatter-described, intent-matched, body-injected — with one difference: a `## Learned` section written by **synthesis over past runs**, not by an author. A document representation buys three things a fact graph cannot: it is **store-agnostic** (a file — no vector DB, no Meko), **legible and hand-editable**, and **diff-able** (synthesis proposes a reviewable change). And consumption rides seams that *work today* (the router's per-turn context assembly), not the dead memory read path.

This ADR is **companion-scoped**: it lands in `daemon/` and applies to the Daemon assistant only. Generalizing playbooks to the coding agent / `core/` is a deliberate later step (Open Question G).

The closed loop:

```
   turn ─► TRIAGE (router): classify scope  +  MATCH playbook(s)
              │                                    │ stamp turn with playbook-id ★
              ▼                                    ▼
        inject matched playbook BODY ──► agent runs the task
              │                                    │ at task end: TaskOutcome ★
              ▼                                    ▼
        episodes accumulate, tagged ★ (playbook-id + outcome)
              │
              ▼
   SYNTHESIS (dmonium idle): per playbook, recall episodes WHERE playbook-id = X
        + outcomes + current body ─► sub-agent proposes a DIFF to `## Learned`
        ─► auto-apply as a git commit   (and spawn NEW playbooks from recurring no-matches)
```

## Decision

1. **A playbook is a self-improving, skill-shaped procedural document at `.dmon/playbooks/<name>.md`.** Frontmatter for *matching* (`name`, `description`, `scope`, optional `triggers`); body for *doing* (a procedure plus a `## Learned` section). It is a **net-new** `.dmon/` asset kind, parallel to `.dmon/agents/` (ADR-020), and **companion-scoped** (lives in and is consumed by `daemon/`). The only structural difference from a Claude Code skill is that `## Learned` is written by the agent (Decision 6), not the author.

2. **A `PlaybookRegistry` auto-discovers playbooks at build time, parallel to `AbilityRegistry`.** It enumerates `.dmon/playbooks/*.md` and exposes `ForScope(scope)` → the scope-matching playbooks (description + body). This mirrors `core/Dmon.Core/Extensions/AbilityRegistry.cs`'s `ForScope`, but lives in `daemon/` (companion-scoped). This **extends ADR-022** (a new auto-discovered registration facet).

3. **Matching is part of triage — one classification, two outputs.** `RouteDecision` (ADR-027) gains `MatchedPlaybookIds: string[]`. The classifier prompt is fed the *scope-filtered* playbook catalog (descriptions, via `PlaybookRegistry.ForScope`) and emits the matched ids alongside `Scope`/`Tier`. No second LLM pass. This **amends ADR-027** (extends `RouteDecision` and the classifier contract).

4. **Consumption is per-turn, via the router — not a session-level prompt builder.** After `ClassifyAndRouteAsync` resolves the matched playbooks, the router **prepends the matched playbook bodies as a system message** to the turn before forwarding to the selected backend — exactly parallel to how it already injects scope-gated tools (`ChatOptions.Tools` via `AbilityRegistry.ForScope`, `TriageRouter.cs`). Playbooks are **turn-dynamic** (the match depends on *this* turn's task); `CLAUDE.md` is session-static. They therefore ride different seams, and the router's per-turn assembly is the correct one. This **amends ADR-027** (the router's output now shapes the message list, not only `ChatOptions`).

5. **The closing wire — routing + outcome persistence.** `RouteDecision` is currently computed and discarded. A new `ITriageEventSink`, invoked by the router after classification, persists a per-turn routing record `{ sessionId, turnId, effectiveScope, matchedPlaybookIds }`. At a task boundary, a `TaskOutcome { playbookId, outcome ∈ {succeeded | corrected | failed | abandoned}, note? }` is recorded and joined to that turn. Synthesis later filters recall (ADR-030) by `playbookId` and reads the outcome. This deliberate, structured outcome marker is the companion's analogue of the coding agent's typed reviewer-verdict — a *labelled* signal, not the fuzzy inference Brain is limited to. This **amends ADR-027** (adds the event sink + persisted routing metadata).

6. **Synthesis is the self-improving loop, hosted by `dmonium` at idle (ADR-028).** Per playbook: gather the recall episodes tagged with its id + their `TaskOutcome`s + the current body; a **scoped single-turn sub-agent** (ADR-010, the `IChatClientFactory` pattern proven by `Dmon.Tools.WebSearch`) proposes a **diff to `## Learned`**. Synthesis also **spawns new playbooks**: recurring *unmatched*-task episodes (clustered from the routing records' no-match descriptors) become a new stub. Recall (ADR-030) is the selection substrate — "every run of playbook X since last pass" is a filtered recall query; the loop is re-runnable because its inputs (Tier-0) are immutable.

7. **Application is auto-apply, git-tracked — that is the autonomy model.** `.dmon/playbooks/` is version-controlled; every synthesis edit and every spawned playbook is a commit. Audit = `git log`/`blame` (Brain's "provenance," for free); rollback = `git revert`. Because a bad edit is one revert away and every diff is human-readable, synthesis may apply **silently** without per-edit confirmation — *silent and fully reversible and legible*, rather than the usual "silent vs propose-a-digest" trade.

8. **Cold-start is both pre-seeded and self-spawned.** A few coarse hand-authored stubs give the router something to match on day one; the spawn path (Decision 6) grows the set from recurring unmatched tasks. Granularity starts **coarse** (too fine → too few runs per playbook for synthesis to learn from).

9. **Store-agnostic by construction; orthogonal to the semantic-fact tier.** The playbook loop touches **no** memory backend — it is files + the router + recall + a sub-agent. It is independent of ADR-026/Meko ("other long-term stores are available"). Any future semantic-fact memory ("user's world") remains a separate, optional concern behind the `ILongTermMemory` *contract* (never Meko types — the standing no-third-party-types-in-API rule).

## Consequences

- **The self-improving loop has a live read path.** Playbook bodies reach the model through the router's working per-turn seam, sidestepping the dead `IMemory.SearchAsync` path entirely. Distillation is no longer writing to a black hole.
- **Improvement is legible and reversible.** "What the agent learned" is a readable Markdown diff in git, not an opaque embedding delta. The autonomy/privacy problem that haunts companion memory is reduced to "read the commit; revert if wrong."
- **One LLM call still does triage.** Playbook matching folds into the existing classification; the cost is a larger classifier prompt (the scope-filtered catalog), not a second pass.
- **Recall (ADR-030) earns a second consumer.** Beyond user-facing "what did we decide," recall is the synthesis selection index, joined by playbook-id. Building it first paid off twice.
- **A structured outcome signal.** The deliberate `TaskOutcome` marker gives synthesis a labelled what-worked/failed signal stronger than Brain's pure inference.
- **New persistence + a registry.** The router gains an event sink and a per-turn routing record (net-new); a `PlaybookRegistry` joins `AbilityRegistry`. Contained, daemon-local additions.
- **Companion-only, for now.** The coding agent does not get playbooks yet, though its typed gate/reviewer signal would make it the *better* host (Open Question G).

## Alternatives

- **Distil facts into a memory store (the path we pivoted from).** Rejected: store-bound (fights "not Meko-specific"), opaque/un-editable, loses the *procedure*, and lands on the dead read path. Procedural knowledge wants a procedural, legible artifact.
- **Consume playbooks via a session-level system-prompt builder (like `CLAUDE.md`).** Rejected: playbooks are turn-dynamic; the matched playbook depends on the turn's triage. The router's per-turn assembly is the correct seam (and a clean session-level builder seam isn't even confirmed to exist — see Open Question D).
- **Static hand-authored skills only (no synthesis).** Rejected: that is just skills. The closed edit-from-runs loop is the entire point ("skills the agent can improve itself").
- **A separate playbook-matching LLM pass.** Rejected: triage already classifies the turn; extending `RouteDecision` reuses that call.
- **Propose-a-digest autonomy.** Rejected as the default: git makes auto-apply safe and reversible. (Retained as an option specifically for *new-playbook spawning*, the higher-stakes action — Open Question F.)

## Open Questions

- **A. Task-boundary detection.** A "task" spans from a triage match until the next match of a different playbook (or session idle/end) — but the precise rule (and how multi-turn tasks are bounded) needs settling before `TaskOutcome` can be reliably timed.
- **B. Outcome emission mechanism.** Is `TaskOutcome` emitted by an explicit agent ability (`record_outcome`), inferred at the boundary by a quick self-assessment, or hybrid (inferred, refined by in-window user corrections)? Lean: hybrid, but undecided.
- **C. Routing-metadata home.** Does the per-turn routing record live in a sidecar routing-event log (joined to recall by turn/session id), as ADR-016 parts metadata, or as a `playbook_id` column on the ADR-030 `recall.db`? Lean: sidecar log (keeps `recall.db` clean), settled in the proposal.
- **D. `SystemPromptBuilder` discrepancy.** Two grounding passes disagreed on whether any session-level system-prompt assembly exists in `core/`. Worth verifying — but it does **not** block: per-turn playbook injection rides the router regardless (Decision 4).
- **E. Matching at scale.** Many playbooks → a large classifier-prompt catalog. A retrieval pre-filter (embed playbook descriptions, shortlist before the classifier) is deferred until the set is large enough to matter.
- **F. New-playbook spawn autonomy.** Editing an existing playbook auto-applies (Decision 7). Should *creating* a new playbook also auto-apply, or surface for one-time approval? Default auto (git-revertible); flagged because it is the higher-stakes write.
- **G. Generalization to the coding agent / `core/`.** Playbooks are companion-scoped here. The coding agent's *typed* gate/reviewer-verdict signal would make it an excellent — arguably better — host. A future change could lift the registry + loop into `core/`.
- **H. Overlapping matches.** When triage matches multiple playbooks, what is the injection order/precedence and is there a cap?

## Relationship to other ADRs

- **ADR-027** — amended: `RouteDecision` gains `MatchedPlaybookIds`; the classifier prompt is fed the scope-filtered playbook catalog; the router injects the matched body per turn (now shaping the message list, not only `ChatOptions`); a new `ITriageEventSink` persists the routing decision + `TaskOutcome` it currently discards. The router stays application policy in `daemon/Daemon.Routing`.
- **ADR-030** — built on, not amended: recall is the synthesis selection substrate (filtered by playbook-id) and gains a second (internal) consumer beyond the user-facing `search_history` tool.
- **ADR-022 / ADR-020** — extended: `.dmon/playbooks/*.md` is a new auto-discovered asset kind; `PlaybookRegistry` is a new registration facet parallel to `AbilityRegistry`.
- **ADR-010** — honoured: the synthesis distiller is a scoped single-turn in-process sub-agent (the `Dmon.Tools.WebSearch` `IChatClientFactory` template), not multi-agent orchestration.
- **ADR-028** — honoured: the synthesis pass is Daemon application policy hosted by `dmonium` at idle; all of this lands in `daemon/`.
- **ADR-026** — independent: playbooks touch no memory backend. Any semantic-fact tier stays orthogonal and behind the `ILongTermMemory` contract; `middleware/` stays empty.
