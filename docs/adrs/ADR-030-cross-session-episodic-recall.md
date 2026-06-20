# ADR-030: Cross-Session Episodic Recall — a Third Memory Tier as a Lossless Projection of the Session Logs

**Date:** 2026-06-20
**Status:** Proposed
**Amends:** ADR-026 (the memory contract-set grows a third tier — `IEpisodicRecall` — alongside `IShortTermMemory`/`ILongTermMemory`; partially closes its Open Question A); ADR-004 (adds a second root-level rebuildable projection, `recall.db`, alongside the existing `sessions.db` cache); ADR-027 (adds a scope-gated recall ability via `IAbilityProvider`)
**Builds on:** ADR-016 (the lossless parts record / `messages.jsonl` is the source the recall index projects from), ADR-028 (the recall ability and its backfill job are Daemon application policy, hosted by `dmonium`), ADR-019/022 (the recall tier is ordinary DI-registered services and an ability provider)

## Context

dmon's memory is, by ADR-016's own design, **event-sourced**: per-session `messages.jsonl` (ADR-004) is the lossless, append-only ground truth, and the memory tiers are *derived projections* of it. ADR-026 named two tiers over that ground truth:

| Tier | Span | Form |
|------|------|------|
| `IShortTermMemory` (sqlite-vec) | **current session only** | verbatim turns |
| `ILongTermMemory` (Meko) | cross-session | **distilled** facts + graph |

Plotting those two on the axes *span* × *form* leaves one cell conspicuously empty:

```
                 verbatim                 distilled
   one session   IShortTermMemory         —
   all sessions  ◄── EMPTY ──►            ILongTermMemory
```

The empty cell — **cross-session *and* verbatim** — is a real, missing capability. A companion agent (the Daemon, ADR-028) routinely needs "what did we actually decide about the Lisbon trip three weeks ago?" The verbatim answer exists, on disk, in some session's `messages.jsonl`. But:

- short-term recall is **bound to the current session** (`MemoryContext.ConversationId` resolves a single `<sessionDir>/index.db`); it cannot see prior sessions; and
- long-term recall returns **distilled** facts (lossy synthesis), not the verbatim moment with provenance.

So the exact words are preserved but **unreachable** — there is no index spanning the whole episodic corpus. Today recall is not even surfaced to the agent at all (the per-session hybrid index is used on the *write* path; there is no agent-callable recall tool — exactly ADR-026 Open Question A).

The grounding confirms the build is small: sessions are enumerable (`ISessionStore.ListAsync()` over the root `sessions.db` registry), and the per-session index schema (`content(entry_id, role, text, timestamp, scope)` + sqlite-vec `float[768]` + FTS5 trigram + model-pin meta, hybrid RRF) **generalizes to a cross-session index by adding one `session_id` column**. The local Nomic-768 embedding generator and the vec/FTS machinery are reused wholesale.

This ADR decides that cross-session verbatim recall is a **third memory tier**, realized as a **single global projection of Tier-0**, surfaced as an **explicit, scope-gated ability**. It is deliberately the *lossless retrieval* layer; the *lossy self-improving/semantic* layer (overnight distillation into a "context graph") is its natural consumer and is **out of scope here** (see Open Question E).

## Decision

1. **A third memory contract, `IEpisodicRecall`, joins the `core/Dmon.Abstractions.Memory` contract-set.** It is cross-session and verbatim — the empty cell above. It is a *sibling* of `IShortTermMemory`/`ILongTermMemory`, **not** folded into `IMemory`'s two-tier fusion: it is neither written by the `RecordAsync` fan-out's distillation path nor merged into the RRF `SearchAsync` over short+long. Recall is a **distinct query path** with its own provenance-rich result type. This **amends ADR-026** (the contract-set described in its Decision 2/3 grows from two tiers to three; the `memory/` "contract + N backends + facade" shape is preserved).

2. **It is backed by one global index, `recall.db`, not a federation of per-session indexes.** `recall.db` is a single sqlite-vec + FTS5 database whose schema is the per-session schema **plus a `session_id` column** (`recall_content(rowid, session_id, entry_id, role, text, timestamp, scope)` + `recall_vec(float[768] cosine)` + `recall_fts(text, trigram)` + `recall_meta(model_id, dimension)`), with one uniform model pin. Per-session federation is rejected: per-session indexes are relocatable (ADR-004), opened one-at-a-time under a session-bound `MemoryContext`, and model-pin-validated individually — querying *N* of them is O(sessions) and a single pin mismatch breaks the merge.

3. **`recall.db` is a rebuildable Tier-0 projection at the sessions root, sibling to `sessions.db`.** Like `sessions.db` (ADR-004's session-index cache), it is a **cache, not source of truth**: it is fully rebuildable from `messages.jsonl` (ADR-016's parts record), and a model-pin change triggers a rebuild — the same discipline the per-session index already applies on mismatch. This **amends ADR-004** by adding a second root-level rebuildable projection; ADR-016 is **honoured** (the index derives from the lossless record, never the reverse).

4. **The recall index is maintained incrementally on the write path, plus a backfill/rebuild command.** Incremental: at `SessionStore.AppendMessagesAsync`'s existing tier fan-out, each newly-appended turn is upserted into `recall.db` (respecting Decision 6's corpus filter). Backfill/rebuild: a command walks `ISessionStore.ListAsync()` and indexes historical sessions; the same path rebuilds from scratch. Backfill is a one-time bulk job suitable for `dmonium` idle time (ADR-028); steady-state per-turn cost is one extra SQLite upsert.

5. **Embeddings are computed once per turn and shared across tiers.** The Nomic-768 vector is hoisted *above* the write-path fan-out and consumed by both the short-term index and `recall.db` — neither tier owns it, so there is no inter-tier coupling and no double-embedding of the `SemaphoreSlim(1,1)`-serialized local generator on the hot path. Invariant: **`recall.db` pins to the same embedding model as short-term** (both are the DI-singleton `LocalEmbeddingGenerator` today); a future per-tier model divergence invalidates vector sharing and is disallowed without a recall rebuild.

6. **The recall corpus is durable turns only; session lifecycle propagates.** `recall.db` indexes `MemoryScope.Agent` and `MemoryScope.User` turns (durable) and **excludes ephemeral `MemoryScope.Session`** turns (the `scope` column already carries this). Deleting a session **evicts** its rows (`DELETE FROM recall WHERE session_id = …`). This makes recall's reach an explicit, auditable function of session retention — the privacy question Tier-0 forces for a personal companion.

7. **Recall is surfaced as one explicit, scope-gated ability — `search_history` — never auto-injected.** It is an `IAbilityProvider` (ADR-027) exposing a single `AITool` the model calls deliberately; it is **not** fused into every turn's context (the cross-session corpus is too large to inline). This **amends ADR-027** by adding a recall ability and **partially closes ADR-026 Open Question A** (an agent-callable memory tool consuming `memory/`). The tool returns provenance-rich, **windowed** hits:

   ```
   RecallHit(SessionId, EntryId, Timestamp, Role, Text, Score, Window[])
   ```

   `Window[]` is the ±k turns surrounding the match within the same session (cheap: `WHERE session_id = … ORDER BY rowid`), so the daemon recovers the *exchange* and can cite the moment ("on May 28th you said …"). Ranking reuses the existing hybrid RRF (KNN + FTS5); recency/time-decay is deferred (Open Question B).

8. **Placement follows the established category discipline.** The `IEpisodicRecall` implementation and `recall.db` live in `memory/Dmon.Memory` (the local sqlite-vec home, ADR-026). The `search_history` ability provider and the backfill job are **Daemon application policy** and live in `daemon/` (gated by the Daemon's scope vocabulary, hosted by `dmonium`) — the same placement ADR-027/028 give the router and other agent-facing policy. The `memory/` contracts gain only the new tier interface; `middleware/` stays empty (ADR-026 D4 upheld).

## Consequences

- **The episodic memory becomes reachable.** The verbatim "what did we decide" moment, previously on disk but unindexed across sessions, is now retrievable with provenance — the single most-missed companion behaviour.
- **The memory model is honestly three-tiered.** short-term (session, verbatim) · long-term (cross-session, distilled) · recall (cross-session, verbatim). The `memory/` contract-plus-backends shape is preserved; the facade gains an optional accessor (Open Question C).
- **Event-sourcing pays off concretely.** Because `recall.db` is a disposable projection of Tier-0, the indexing/ranking algorithm can evolve and re-derive — v2 re-indexes from the same logs. This is the same property ADR-016 already bought; this ADR is the first to spend it.
- **Recall's reach equals retention, by construction.** Corpus = durable scopes; session-delete evicts. There is no hidden shadow copy beyond Tier-0 itself; deletion is honest.
- **One extra SQLite write per turn; one bulk backfill.** Steady state is negligible (shared embedding, single upsert). Backfilling a long history is a one-time serialized-embedding cost, deliberately an idle-time job.
- **It is the substrate for the parked self-improving layer.** The deferred overnight semantic consolidation (the "Brain"-style context graph) *queries this recall index* to find episodes worth distilling. Building lossless retrieval first is the right order.

## Alternatives

- **Query-time federation of per-session indexes (no global index).** Rejected (Decision 2): O(sessions) per query, fragile against relocatable sessions and per-index model pins.
- **Overload `IShortTermMemory` with a cross-session mode.** Rejected (Decision 1): short-term is bound to one session via `MemoryContext.ConversationId`; recall operates at the agent/user level across all sessions, returns a different (provenance-rich) hit, and has different lifecycle semantics. A distinct contract is cleaner than a flag that means "ignore the ambient session."
- **Auto-inject recalled context into every turn (recall-as-middleware, ADR-026 OQ-A's other half).** Rejected (Decision 7): the cross-session corpus is too large to inline; the model should *reach* for history deliberately. An always-on recall middleware remains possible later and would consume this same tier.
- **Distil-only (rely on `ILongTermMemory` for "what did we decide").** Rejected: distillation is lossy and unprovable; a companion citing "you said X on Tuesday" needs the verbatim turn, not a paraphrase.
- **Re-embed per tier (keep recall fully independent of the short-term write path).** Rejected (Decision 5): the local generator is serialized; double-embedding every turn is a real hot-path cost. Hoisting the embedding above the fan-out gives independence *and* single-embed.
- **Put the recall ability in `memory/` or `tools/`.** Rejected (Decision 8): the ability is agent-facing application policy gated by the Daemon's scope vocabulary — the ADR-027/028 home for such policy is `daemon/`. The `memory/` package stays a backend; `tools/` stays protocol-keyed `IToolExtension` packages.

## Open Questions

- **A. Router (Personal/World) invocation-scope gating.** Decision 6 filters *what is in* the corpus (`MemoryScope`). *Which turns may invoke* `search_history` (the ADR-027 router scope vocabulary) is an additive gate, deferred to the proposal/a later change.
- **B. Recency / time-decay ranking.** v1 reuses hybrid RRF unchanged. Whether to blend a recency prior (or expose a `timeRange` filter as more than a hard cut) is deferred.
- **C. Facade exposure.** Whether `IEpisodicRecall` hangs off the `IMemory` facade (an accessor, like `ShortTerm`/`LongTerm`) or stands alone as a DI service consumed only by the ability provider — an implementation choice for the proposal. Lean: standalone, since it is outside the `RecordAsync`/`SearchAsync` fusion.
- **D. Window size.** Default ±k (small, e.g. 2) and whether k is per-call or configured — a proposal detail.
- **E. The self-improving / semantic consolidation layer.** The parked "overnight distillation into a context graph" thread (Perplexity-"Brain"-style) is **explicitly out of scope**; this ADR builds its lossless substrate. It will be its own ADR/change.
- **F. Tier-0 retention policy.** A companion's `messages.jsonl` is its entire history, verbatim and unbounded. Pruning/retention of Tier-0 itself (which would also shrink recall's reach) is a policy question this ADR surfaces but does not decide.
- **G. Single-agent reach.** Recall spans one `AgentId`'s sessions. Cross-agent recall is out of scope (and would interact with ADR-028's agent isolation).

## Relationship to other ADRs

- **ADR-026** — amended: the memory contract-set grows a third tier (`IEpisodicRecall`); the recall index is a new `memory/Dmon.Memory` backend under the same family; `middleware/` stays empty (D4 upheld). **Partially closes Open Question A** (agent-callable memory tool) for the verbatim-recall half; the recall-injection-middleware half remains open.
- **ADR-004** — amended: adds `recall.db` as a second root-level rebuildable cache alongside `sessions.db`; the relocatable-session-directory model is otherwise unchanged.
- **ADR-016** — honoured: `recall.db` is a derived projection of the lossless parts record; the record stays canonical and the projection rebuildable from it.
- **ADR-027** — amended: adds a scope-gated `search_history` ability via `IAbilityProvider`/`AbilityRegistry`; recall as application policy lives with the agent, not in `middleware/`.
- **ADR-028** — honoured: the recall ability and its backfill/rebuild job are Daemon application policy hosted by `dmonium` (idle-time backfill); the `memory/` backend stays a backend.
- **ADR-019 / ADR-022** — the recall tier and ability are ordinary DI-registered services / an ability provider; no new hosting mechanism.
