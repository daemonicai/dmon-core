# Project Puck — Sleep-Time Memory Consolidation

**A specification and requirements document**

Version 0.1 (draft) · Target platform: .NET 10 / C# 14 · LLM abstraction: `Microsoft.Extensions.AI`

---

## 1. Overview

Project Puck is an offline memory-consolidation subsystem for a long-running conversational agent. While the primary agent is idle ("asleep"), the system processes the day's persisted session transcripts and folds them into a durable, semantically searchable long-term memory store.

The system is built on two named planes — introduced fully in §4 — that together give the project its name:

- **Titania** — the control plane and trust boundary. She triggers runs, stages the day's sessions, dispatches work, and owns the **commit gate**: the single point through which anything reaches durable memory.
- **Puck** — the per-run reasoning executor. He does the night legwork — extract, retrieve, argue — and *proposes* verdicts. He is allowed to be wrong; nothing he produces is durable until Titania has validated it.

The arrangement is *Puck proposes, Titania disposes*. It places all fallible, model-driven reasoning on one side of a line and all safety-critical, deterministic, must-not-fail writes on the other.

The distinctive mechanism is an **asymmetric two-model dialogue**. Rather than a single summarisation pass, two small local models converse about the day's material, each anchored to a different source of truth:

- **Agent A (the Narrator)** holds the day's raw sessions in context and argues from *novelty* — "this happened, this matters, this is new."
- **Agent B (the Archivist)** holds no fresh sessions. It argues from *continuity*, retrieving from existing long-term memory on demand — "we already knew that," "that contradicts what's on record," "that's the third time this has come up."

The friction between novelty and continuity is the engine. It produces the behaviours that make memory feel intelligent — deduplication, reinforcement, contradiction detection, and cross-session synthesis — while a strict forcing function and grounding requirement suppress the confabulation that unconstrained model-to-model chat tends to produce.

### 1.1 Why asymmetry, not symmetry

Two identical models with identical context "discussing" is one model talking to itself with added latency and variance; their agreement is cheap and carries no information. Worse, they amplify each other's errors: if A invents a connection, B has no independent reason to doubt it, and an invented insight gets written to memory as a discovered pattern. The asymmetry — A grounded in today, B grounded in the existing store — gives the two sides genuinely different positions to argue from, so the dialogue does real work and is far harder to derail into mutual confabulation.

This matters more, not less, with **small local models**, which are weaker at resisting consensus spirals and at staying grounded. The structural constraints below (the forcing function, the citation requirement, the asymmetric roles) are therefore load-bearing, not polish.

---

## 2. Goals and non-goals

### 2.1 Goals

- Consolidate persisted session transcripts into durable long-term memory without human involvement.
- Detect and correctly resolve **reinforcements**, **contradictions**, and **cross-session links** against existing memory.
- Produce a **semantically searchable** store usable by the primary agent at inference time.
- Keep every derived memory **traceable to its source** (which session, which turn, which consolidation run).
- Keep consolidation cost bounded by **the day's footprint**, not the total size of memory.
- Run entirely on **local models** via `IChatClient`, with no dependency on a hosted provider.

### 2.2 Non-goals

- Real-time / in-conversation memory writes (the system is strictly offline/batch).
- Replacing the primary agent's working context window.
- A general-purpose knowledge base for arbitrary external documents.
- Multi-user or multi-tenant isolation (single-subject memory is assumed for v0.1; see §13).

---

## 3. Glossary

| Term | Meaning |
|------|---------|
| **Session** | A single primary-agent conversation, persisted to disk as an ordered list of turns. |
| **Episodic record** | A near-raw, immutable record of what was said in a session. The ground truth the system may always re-derive from. |
| **Semantic memory** | A derived, consolidated belief about the subject or world (e.g. an entity attribute, a fact, a preference). Mutable via supersession only. |
| **Claim** | A candidate proposition extracted from the day's sessions, with pointers to supporting turns. The unit of work in the dialogue. |
| **Verdict** | The dialogue's decision about a claim: `New`, `Reinforce`, `Contradict`, `Link`, or `Discard`. |
| **Manifest** | A compact, resident table-of-contents of memory: known entities and threads with counts and recency, but not their contents. |
| **Entity** | A normalised referent (a person, place, project, etc.) that claims and memories attach to. |
| **Consolidation run** | One execution of the pipeline over a defined set of sessions, with its own identifier and provenance. |
| **Titania** | The control plane and trust boundary: triggering, staging, dispatch, and the commit gate. The only component that writes to durable stores. |
| **Puck** | The per-run reasoning executor: runs extract → retrieve → argue, housing the Narrator and Archivist agents, and proposes verdicts. Has no direct write access. |

---

## 4. Architecture overview

```
┌─────────────────────────────────────────────────────────────────────┐
│ TITANIA  —  control plane & trust boundary                            │
│ trigger · stage sessions · dispatch run · COMMIT GATE · durable write │
└──────┬─────────────────────────────────────────────▲──────────────────┘
       │ dispatch run                                 │ proposed verdicts
       ▼                                              │ (validated + committed here)
┌──────┴──────────────────────────────────────────────┴────────────────┐
│ PUCK  —  per-run reasoning executor  (proposes; cannot write)         │
│   ┌──────────────────┐    dialogue turns    ┌──────────────────┐      │
│   │ Narrator (A)     │◀────────────────────▶│ Archivist (B)    │      │
│   │ today's sessions │                      │ retrieval tools  │      │
│   │ argues novelty   │                      │ argues continuity│      │
│   └──────────────────┘                      └────────┬─────────┘      │
└───────────────────────────────────────────────────────┼──────────────┘
                                            read-only     │
                                                          ▼
                                          ┌───────────────────────────┐
                                          │ Retrieval layer (hybrid:   │
                                          │ vector + lexical + struct) │
                                          └─────────────┬─────────────┘
                              ┌─────────────────────────┴───────────┐
                              ▼                                      ▼
                ┌──────────────────────┐          ┌──────────────────────────┐
                │ Episodic store       │          │ Semantic store + index   │
                │ (immutable, append)  │          │ (vector + metadata +     │
                │                      │          │  manifest + entities)    │
                └──────────▲───────────┘          └───────────▲──────────────┘
                           └──── Titania writes (commit gate only) ────┘
```

### 4.1 Two planes: Puck proposes, Titania disposes

The system is split at its most important seam — the **trust boundary** between fallible reasoning and durable state. The reasoning is performed by small local models that can confabulate. The commit, by contrast, must enforce invariants that cannot be allowed to fail: grounding must resolve to real source text, contradictions must supersede rather than overwrite, reinforcements must not double-count. So all model-driven, fallible work sits on one side of the line and all safety-critical, deterministic plain-code work on the other.

| Component | Plane | Why |
|-----------|-------|-----|
| Trigger, scheduling, run lifecycle | **Titania** | When/whether to run; not reasoning. |
| Staging the day's sessions, dispatching work | **Titania** | Directs; does not deliberate. |
| `extract → retrieve → argue` for a run | **Puck** | The actual night legwork. |
| Narrator (A) and Archivist (B) | **inside Puck** | The two voices are *how* Puck reasons. |
| Grounding validation (the commit gate) | **Titania** | Titania checking Puck's work before it lands. |
| Commit / supersession / provenance / idempotence | **Titania** | The durable, must-not-fail writes. |
| Retrieval layer + stores | shared infrastructure | The wood both move through; Puck reads, Titania writes. |

**Puck emits verdicts; Titania validates and commits them.** Puck is *expected* to be occasionally wrong — that is the nature of small models — because nothing he proposes touches durable memory until Titania has checked that the grounding resolves and the invariants hold. This is the drift-prevention argument from first principles, given a clean home: the trust boundary *is* the Titania/Puck line. The boundary would be worth having even without the names.

The split also yields a free parallelism story (not required for v0.1): one Titania can dispatch several Pucks — across runs, or across batches of claims within a run — while remaining the single serialization point for commits.

> **Naming note.** The two *planes* take literary names because they are load-bearing concepts referred to constantly. The two *agents inside Puck* keep functional names — Narrator and Archivist — deliberately: theming them as play characters too would turn the codebase into a crossword. Structure earns the costume; internal parts keep names that say what they do.

```
                    ┌──────────────────────────────────────────────┐
                    │              Titania (control)                │
                    │  (trigger, staging, dispatch, commit gate)    │
                    └───────┬───────────────────────────────┬───────┘
                            │                               │
                  ┌─────────▼─────────┐           ┌─────────▼─────────┐
   sessions ─────▶│  Agent A          │◀─dialogue▶│  Agent B          │
   (today)        │  "Narrator"       │   turns   │  "Archivist"      │
                  │  IChatClient      │           │  IChatClient      │
                  │  + extraction     │           │  + retrieval tools│
                  └───────────────────┘           └─────────┬─────────┘
                            │                               │ tool calls
                            │ proposed verdicts             ▼
                            ▼                     ┌───────────────────┐
                  ┌───────────────────┐           │  Retrieval layer  │
                  │  Titania commit   │──────────▶│  (hybrid: vector  │
                  │  gate (validate + │  read     │   + lexical +     │
                  │  supersede + prov)│◀─────────│   structured)     │
                  └─────────┬─────────┘           └─────────┬─────────┘
                            │                               │
                            ▼                               ▼
              ┌──────────────────────┐        ┌──────────────────────────┐
              │  Episodic store      │        │  Semantic store + index  │
              │  (immutable, append) │        │  (vector + metadata +    │
              │                      │        │   manifest + entities)   │
              └──────────────────────┘        └──────────────────────────┘
```

### 4.2 The pipeline is `extract → retrieve → argue → commit`

The "conversation" is only the third stage. It cannot run first, because B cannot retrieve the relevant slice of memory until it knows what the day was *about* — and determining that is itself part of consolidation. The ordering resolves the chicken-and-egg:

1. **Extract.** Agent A reads the day's sessions and emits candidate **claims**, each tagged with the entities it concerns and pointers to the source turns. These candidates *are* the retrieval keys.
2. **Retrieve.** For each claim, the retrieval layer fetches the relevant memory neighbourhood (reinforcements and likely contradictions) plus its metadata (counts, recency, confidence, supersession history).
3. **Argue.** A and B walk the claim list. For each claim, A asserts from novelty, B responds from the retrieved continuity evidence, and the exchange terminates in a **verdict** — never an open-ended feeling.
4. **Commit.** *Titania's commit gate* applies verdicts to the stores: append new episodic records always; append/supersede semantic memories per verdict, with full provenance. Puck never writes; he hands proposals to the gate.

Because every claim derives from something today touched, and the day touched a bounded number of entities, the working set per run is bounded by today's footprint. A year of accumulated memory does not slow tonight's run; it only makes each retrieval slightly denser.

---

## 5. Triggers — what "asleep" means

Project Puck supports two trigger modes, and **both should be enabled**:

- **R-TRIG-1 — Session-end consolidation.** When a session is marked complete, its episodic record is written and a *lightweight* per-session extraction may run. Per-session processing alone cannot see cross-session structure, so it is not sufficient on its own.
- **R-TRIG-2 — Nightly sweep.** A scheduled batch re-consolidates across *all* of the day's sessions together. This is where cross-session synthesis lives (noticing that three separate conversations concerned one project), and is the primary mode.

Idle-timeout triggering is explicitly **not** used as the sole mechanism: it tends to consolidate half-finished sessions as though they were complete. If an idle trigger is desired, it must only schedule episodic capture, never semantic commitment.

---

## 6. Data model

### 6.1 Episodic store (immutable)

Append-only. Never edited or destructively deleted by consolidation. This separation is the primary defence against semantic drift: derived memory can always be re-derived from episodic ground truth, so a bad consolidation run is recoverable.

```csharp
public sealed record EpisodicRecord
{
    public required Guid Id { get; init; }
    public required string SessionId { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required IReadOnlyList<Turn> Turns { get; init; }   // role + text, ordered
    public string? RawSummary { get; init; }                   // optional cheap summary
}

public sealed record Turn(int Index, string Role, string Text);
```

### 6.2 Semantic store (derived, supersession-only)

```csharp
public sealed record SemanticMemory
{
    public required Guid Id { get; init; }
    public required string Text { get; init; }                 // the belief, in normalised form
    public required MemoryType Type { get; init; }             // Fact, Preference, Event, Relationship, ...
    public required IReadOnlyList<string> EntityIds { get; init; }

    // B's ammunition — provenance and confidence
    public required double Confidence { get; init; }           // [0,1]
    public required int ReinforcementCount { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
    public required IReadOnlyList<Provenance> Sources { get; init; }

    // supersession (no destructive edits)
    public Guid? Supersedes { get; init; }
    public Guid? SupersededBy { get; init; }
    public required string ConsolidationRunId { get; init; }

    // embedding handled by the vector index, not stored inline here
}

public sealed record Provenance(string SessionId, int TurnIndex, string ConsolidationRunId);
```

**R-DATA-1.** Semantic memories are never edited in place. A change of belief is recorded by writing a new memory whose `Supersedes` points at the old one, and setting the old one's `SupersededBy`. The old memory remains queryable for audit and re-derivation.

**R-DATA-2.** Every semantic memory carries at least one `Provenance` entry. A memory with no traceable source is a defect.

### 6.3 Entity registry

```csharp
public sealed record Entity
{
    public required string Id { get; init; }                   // stable, normalised
    public required string CanonicalName { get; init; }
    public required IReadOnlyList<string> Aliases { get; init; } // "Sarah", "his wife", "the partner"
    public required int MentionCount { get; init; }
    public required DateTimeOffset LastSeen { get; init; }
}
```

### 6.4 Manifest (resident table of contents)

The one structure the system keeps in working memory across the dialogue. It records the *existence* of entities and threads — not their contents — so B can answer "have we seen this before?" cheaply, and reserve expensive retrieval for the hits and the contradiction suspects.

```csharp
public sealed record ManifestEntry(
    string EntityId,
    string CanonicalName,
    int MentionCount,
    DateTimeOffset LastSeen,
    ManifestTier Tier);                 // Hot (full), Warm, Cold (collapsed to a count)
```

**R-DATA-3.** The manifest is tiered: hot/recent entities are listed in full; the cold long tail is collapsed to aggregate counts so the manifest does not itself grow without bound. It has its own summarisation budget.

---

## 7. The agents

Both agents are `IChatClient` instances. They may wrap the **same** underlying local model; their roles are defined by distinct system prompts, options, and — for B — tools. Sharing the model keeps the resource footprint low; the asymmetry comes from prompt and tooling, not from different weights.

### 7.1 Agent A — Narrator

- **Holds:** the day's raw sessions (or a working window of them) in context.
- **Argues from:** novelty and salience.
- **Responsibilities:** the extraction pass (emit claims with entity tags and turn pointers); during the dialogue, assert what is new and why it matters; on B's challenge, defend or retract a claim **by pointing at the supporting turn**.
- **Tools:** none required (it has its material in context). May expose a `quote_turn(sessionId, turnIndex)` helper to make grounding explicit.

### 7.2 Agent B — Archivist

- **Holds:** nothing fresh. The manifest is resident; everything else is retrieved on demand.
- **Argues from:** continuity, using retrieved metadata as evidence ("held at high confidence across four sessions since January, never contradicted").
- **Responsibilities:** challenge each claim against the existing store; classify it as reinforcement, contradiction, duplicate, or genuinely new; surface counts and recency that A cannot see.
- **Tools (registered as `AIFunction`s):**
  - `search_memory(query, k)` — hybrid retrieval, returns memories + metadata.
  - `get_entity(entityId)` — entity record, mention count, recency.
  - `find_contradictions(claim)` — near-neighbour search filtered to same entity + attribute slot with incompatible value.
  - `manifest_lookup(name)` — cheap existence/count check.

> **Design note.** B is *the model with a retrieval tool over the store*, not a model with memory loaded into its context. Loading memory into context would make B window-bound, stale the instant memory updates, and turn "how much to load" into an unwinnable trade-off. Retrieval makes the working set per turn equal to *one claim's neighbourhood plus the manifest* — never the day's full union, never the whole store.

---

## 8. The dialogue protocol

### 8.1 Forcing function

The two models are **not** chatting open-endedly. They walk the candidate claim list and, for each claim, must converge on a structured **verdict**. The conversation terminates in a decision, not a vibe.

```csharp
public enum VerdictKind { New, Reinforce, Contradict, Link, Discard }

public sealed record Verdict
{
    public required string ClaimId { get; init; }
    public required VerdictKind Kind { get; init; }
    public required string Rationale { get; init; }
    public Guid? TargetMemoryId { get; init; }      // for Reinforce/Contradict/Link
    public required IReadOnlyList<Provenance> Grounding { get; init; }  // mandatory
    public required double Confidence { get; init; }
}
```

### 8.2 Grounding requirement

**R-DLG-1.** Every verdict must carry at least one `Grounding` entry pointing at a real source turn (for the novelty side) and, where applicable, a real `TargetMemoryId` (for the continuity side). A verdict whose grounding does not resolve to existing text is rejected and the claim is re-examined or discarded. This citation requirement is the single strongest brake on the confabulation spiral.

### 8.3 Turn budget and termination

**R-DLG-2.** Each claim has a bounded exchange (e.g. ≤ N back-and-forth turns; default `N = 4`). If no verdict is reached within budget, the claim defaults to `New` at reduced confidence **and** is flagged for review, rather than being silently dropped or silently committed.

**R-DLG-3.** B may issue an ad-hoc retrieval mid-dialogue when surprised ("did Berlin ever come up before?"), but such queries are rate-limited per claim (default ≤ 2) to prevent retrieval spirals.

**R-DLG-4.** To reduce stalling, B pre-fetches each claim's neighbourhood before its turn, plus a small look-ahead for the next claim.

### 8.4 Verdict semantics at commit time

| Verdict | Commit action |
|---------|---------------|
| `New` | Insert a new `SemanticMemory` with `ReinforcementCount = 1`. |
| `Reinforce` | Bump `ReinforcementCount`, raise `Confidence` (bounded), update `LastSeen`, append `Provenance`. No new belief text. |
| `Contradict` | Insert a new memory; set its `Supersedes` to the target; set target's `SupersededBy`. Never delete the target. |
| `Link` | Attach the claim to an existing entity/thread cluster; may insert a relationship memory. |
| `Discard` | No semantic write (episodic record is still retained). |

---

## 9. Retrieval layer

### 9.1 The load-bearing failure mode: retrieval miss reads as novelty

If today's phrasing does not match how a memory was stored, B retrieves nothing, concludes "new," and a duplicate is written — or worse, a contradiction is missed. The entire continuity side goes **silently blind exactly when it matters most**. The false-novelty rate is therefore the ceiling on the whole design. Three mitigations are mandatory:

- **R-RET-1 — Query expansion.** A generates a small set of paraphrases per claim; retrieval runs against all of them so recall does not hinge on one phrasing.
- **R-RET-2 — Entity normalisation before query.** Aliases ("Sarah", "his wife", "the partner") must resolve to one `Entity` *before* lookup, or each will miss the others' history.
- **R-RET-3 — Hybrid retrieval.** Vector search for fuzzy recall **and** lexical/structured search for exact entity/attribute matches, because the two fail on different inputs and cover each other.

### 9.2 A convenient property

Contradictions are *semantically close* to what they contradict ("I live in Munich" vs "I live in Berlin" — same structure, entity, and attribute slot). Vector search over a claim naturally surfaces both reinforcements and contradictions in one pull; B's job is to notice that a high-similarity neighbour holds an *incompatible value*. Counting ("third time") is what vector search is bad at — that is delegated to entity-keyed lookup and the manifest. The two retrieval modes split the labour cleanly.

### 9.3 Interfaces

The semantic index is expressed against the `Microsoft.Extensions.VectorData` abstractions so the concrete store (e.g. Qdrant, SQLite-vec, Postgres/pgvector, or in-memory for tests) is swappable. Embeddings are produced via `IEmbeddingGenerator<string, Embedding<float>>` from `Microsoft.Extensions.AI`.

```csharp
public interface IMemoryRetrieval
{
    Task<IReadOnlyList<RetrievedMemory>> SearchAsync(
        string query, int k, EntityFilter? filter = null, CancellationToken ct = default);

    Task<IReadOnlyList<RetrievedMemory>> FindContradictionsAsync(
        Claim claim, CancellationToken ct = default);

    Task<Entity?> GetEntityAsync(string entityId, CancellationToken ct = default);

    Task<IReadOnlyList<ManifestEntry>> ManifestLookupAsync(
        string name, CancellationToken ct = default);
}

public sealed record RetrievedMemory(SemanticMemory Memory, double Score);
```

---

## 10. Forgetting

A store that only accumulates becomes slower and noisier forever. Forgetting is in scope for v0.1 and must be designed in, not retrofitted.

- **R-FORGET-1 — Salience scoring.** Each memory carries a salience derived from reinforcement count, recency, and type. Low-salience memories decay.
- **R-FORGET-2 — Decay.** Confidence/salience decays over time absent reinforcement; below a floor, a memory becomes eligible for archival (moved out of the hot index, retained in cold storage — never hard-deleted by the system).
- **R-FORGET-3 — Abstraction.** Many episodic events that generalise to one semantic fact may be consolidated into a single semantic memory; the originals remain in the episodic store and may be dropped from the *hot* semantic index.

> Permanent deletion is out of scope for the automated pipeline. The system archives; it does not erase.

---

## 11. .NET 10 implementation with `Microsoft.Extensions.AI`

### 11.1 Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.AI` | `IChatClient`, middleware pipeline, function-invocation. |
| `Microsoft.Extensions.AI.Abstractions` | core abstractions (`ChatMessage`, `ChatOptions`, `AIFunction`, `IEmbeddingGenerator`). |
| local provider (e.g. `OllamaSharp` via `.AsIChatClient()`, or an `Microsoft.Extensions.AI` Ollama/ONNX integration) | the local model behind `IChatClient`. |
| `Microsoft.Extensions.VectorData.Abstractions` + a provider | the semantic vector index. |
| `Microsoft.Extensions.Hosting` | the scheduled nightly worker. |

### 11.2 Building the two clients

Each agent is an `IChatClient` built with a `ChatClientBuilder` pipeline. Function-invocation middleware gives B its retrieval loop; logging/telemetry and optional caching wrap both.

```csharp
using Microsoft.Extensions.AI;

// One underlying local model, shared by both roles.
IChatClient baseClient = new OllamaApiClient(
        new Uri("http://localhost:11434/"), "qwen2.5:7b-instruct")
    .AsIChatClient();

// Agent A — Narrator. No tools; argues from sessions in context.
IChatClient narrator = new ChatClientBuilder(baseClient)
    .UseLogging(loggerFactory)
    .UseOpenTelemetry(sourceName: "puck.narrator")
    .Build();

// Agent B — Archivist. Retrieval tools + automatic function invocation.
IChatClient archivist = new ChatClientBuilder(baseClient)
    .UseLogging(loggerFactory)
    .UseOpenTelemetry(sourceName: "puck.archivist")
    .UseFunctionInvocation()          // drives the tool-call loop for B
    .Build();
```

### 11.3 Registering B's retrieval tools

```csharp
var retrievalTools = new List<AITool>
{
    AIFunctionFactory.Create(
        (string query, int k) => retrieval.SearchAsync(query, k),
        name: "search_memory",
        description: "Hybrid search over long-term memory. Returns memories with confidence, " +
                     "reinforcement count, first/last seen, and provenance."),
    AIFunctionFactory.Create(
        (string entityId) => retrieval.GetEntityAsync(entityId),
        name: "get_entity",
        description: "Look up an entity's canonical record, mention count, and recency."),
    AIFunctionFactory.Create(
        (Claim claim) => retrieval.FindContradictionsAsync(claim),
        name: "find_contradictions",
        description: "Find memories about the same entity+attribute with an incompatible value."),
    AIFunctionFactory.Create(
        (string name) => retrieval.ManifestLookupAsync(name),
        name: "manifest_lookup",
        description: "Cheap existence/count check against the resident manifest."),
};

var archivistOptions = new ChatOptions
{
    Tools = retrievalTools,
    Temperature = 0.2f,          // continuity side is conservative
    ToolMode = ChatToolMode.Auto
};
```

### 11.4 Extraction with structured output

The extraction pass uses the typed `GetResponseAsync<T>` overload to get claims back as schema-validated objects rather than free text.

```csharp
var extractionPrompt = new List<ChatMessage>
{
    new(ChatRole.System, PuckPrompts.Extraction),   // "emit claims; tag entities; cite turns"
    new(ChatRole.User, RenderSessions(todaysSessions))
};

ChatResponse<ClaimSet> extracted = await narrator.GetResponseAsync<ClaimSet>(
    extractionPrompt,
    useJsonSchemaResponseFormat: true,
    cancellationToken: ct);

IReadOnlyList<Claim> claims = extracted.Result.Claims;
```

```csharp
public sealed record ClaimSet(IReadOnlyList<Claim> Claims);

public sealed record Claim(
    string Id,
    string Text,
    IReadOnlyList<string> EntityMentions,
    IReadOnlyList<Provenance> Grounding,
    double Salience);
```

### 11.5 Puck — the dialogue loop (proposes verdicts)

This is Puck's core. A single shared `List<ChatMessage>` carries the per-claim exchange. Puck alternates the two clients, enforces the turn budget, and finishes by asking for a typed verdict — but it returns a **proposal**. Puck performs no grounding validation and holds no write access; that is Titania's job (§11.6).

```csharp
// Inside Puck — the per-run reasoning executor.
async Task<ProposedVerdict> DeliberateAsync(Claim claim, CancellationToken ct)
{
    // Pre-fetch B's evidence so it doesn't stall on the first turn (R-DLG-4).
    var neighbourhood = await retrieval.FindContradictionsAsync(claim, ct);

    var convo = new List<ChatMessage>
    {
        new(ChatRole.System, PuckPrompts.DialogueRules),
        new(ChatRole.User, RenderClaimAndEvidence(claim, neighbourhood))
    };

    for (var turn = 0; turn < MaxTurnsPerClaim; turn++)
    {
        // Narrator asserts/defends from novelty, citing source turns.
        var aMsg = await narrator.GetResponseAsync(convo, narratorOptions, ct);
        convo.AddMessages(aMsg);

        // Archivist challenges from continuity; may call retrieval tools (rate-limited).
        var bMsg = await archivist.GetResponseAsync(convo, archivistOptions, ct);
        convo.AddMessages(bMsg);

        if (LooksConverged(bMsg)) break;
    }

    // Forcing function: terminate in a typed verdict (R-DLG-1). This is a PROPOSAL —
    // Puck does not validate grounding or commit. The dialogue transcript travels with
    // it so Titania (and audit) can see the rationale.
    convo.Add(new(ChatRole.User, PuckPrompts.EmitVerdict));
    ChatResponse<Verdict> v = await archivist.GetResponseAsync<Verdict>(
        convo, useJsonSchemaResponseFormat: true, cancellationToken: ct);

    return new ProposedVerdict(v.Result, Transcript: convo);
}
```

```csharp
public sealed record ProposedVerdict(Verdict Verdict, IReadOnlyList<ChatMessage> Transcript);
```

### 11.6 Titania — supervisor and commit gate (the trust boundary)

Titania owns the run lifecycle *and* the only path to durable storage. She dispatches a run to Puck, receives proposals, and passes every one through the **commit gate** before anything lands. The gate is plain deterministic code — no model in the loop — so the must-not-fail invariants are enforced by construction.

```csharp
public sealed class Titania(
    Puck puck,
    IMemoryWriter writer,            // the ONLY component with write access
    ISessionStaging staging,
    TimeProvider clock,
    ILogger<Titania> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var delay = NextRunDelay(clock.GetUtcNow());      // e.g. 03:00 local (R-TRIG-2)
            await Task.Delay(delay, ct);

            var runId = Guid.NewGuid().ToString("N");
            using var scope = log.BeginScope("run {RunId}", runId);
            try   { await RunAsync(runId, ct); }
            catch (Exception ex) { log.LogError(ex, "consolidation run failed"); }
        }
    }

    public async Task RunAsync(string runId, CancellationToken ct)
    {
        var sessions = await staging.StageTodaysSessionsAsync(ct);   // Titania directs
        var proposals = await puck.DeliberateRunAsync(sessions, runId, ct);  // Puck proposes

        foreach (var proposal in proposals)                          // Titania disposes
            await CommitAsync(proposal, runId, ct);
    }

    // THE TRUST BOUNDARY. Nothing reaches durable memory except through here.
    async Task CommitAsync(ProposedVerdict proposal, string runId, CancellationToken ct)
    {
        // R-DLG-1: grounding must resolve to real source text / a real target memory,
        // else the proposal is rejected and downgraded to a reviewable New (R-DLG-2).
        Verdict verdict = ValidateGrounding(proposal.Verdict)
            ?? DowngradeToReviewableNew(proposal);

        EnforceInvariants(verdict);   // supersession-not-overwrite (R-DATA-1),
                                      // provenance present (R-DATA-2),
                                      // idempotence by (claimId, runId) (N-7)

        await writer.ApplyAsync(verdict, proposal.Transcript, runId, ct);
    }
}
```

The dialogue transcript is persisted alongside the committed memory as its rationale (N-6), so three weeks later one can read *why* a given memory was written.

### 11.7 Embeddings

```csharp
IEmbeddingGenerator<string, Embedding<float>> embedder =
    new OllamaApiClient(new Uri("http://localhost:11434/"), "nomic-embed-text")
        .AsIEmbeddingGenerator();

ReadOnlyMemory<float> vector =
    (await embedder.GenerateAsync([memory.Text], cancellationToken: ct))[0].Vector;
```

> **API note.** The `IChatClient` surface used above (`GetResponseAsync`, the typed `GetResponseAsync<T>` overload with `useJsonSchemaResponseFormat`, `GetStreamingResponseAsync`, `ChatClientBuilder`, `.UseFunctionInvocation()`, `AIFunctionFactory.Create`) is current as of the .NET 10 `Microsoft.Extensions.AI` release. Exact attribute/type names in `Microsoft.Extensions.VectorData` should be confirmed against the package version pinned at build time, as that surface has evolved across previews.

---

## 12. Requirements summary

### 12.1 Functional

- **F-1.** The system shall persist every completed session as an immutable episodic record before any semantic processing.
- **F-2.** The system shall run a nightly cross-session consolidation over all of the day's sessions (R-TRIG-2).
- **F-3.** Extraction shall emit claims tagged with entity mentions and source-turn provenance.
- **F-4.** For each claim, the system shall conduct an asymmetric A/B dialogue terminating in a typed verdict.
- **F-5.** The system shall correctly classify claims as New, Reinforce, Contradict, Link, or Discard, and apply the corresponding commit action (§8.4).
- **F-6.** Contradictions shall be resolved by supersession, never destructive edit (R-DATA-1).
- **F-7.** Retrieval shall be hybrid (vector + lexical/structured) with query expansion and entity normalisation (R-RET-1..3).
- **F-8.** The system shall maintain a tiered resident manifest for cheap existence/count checks (R-DATA-3).
- **F-9.** The system shall apply salience-based decay and abstraction; archival, not deletion (R-FORGET-1..3).
- **F-10.** The primary agent shall be able to query the semantic store at inference time via `IMemoryRetrieval`.

### 12.2 Non-functional

- **N-1 (Locality).** All models run locally behind `IChatClient`; no hosted-provider dependency at runtime.
- **N-2 (Bounded cost).** Per-run cost scales with the day's footprint, not total memory size.
- **N-3 (Auditability).** Every semantic memory is traceable to source turns and a consolidation run.
- **N-4 (Recoverability).** Derived memory can be fully re-derived from the episodic store.
- **N-5 (Drift resistance).** No consolidation output is committed without resolvable grounding.
- **N-6 (Observability).** Each run emits OpenTelemetry traces; each claim's dialogue transcript is retained as the rationale for its verdict.
- **N-7 (Idempotence).** Re-running consolidation over the same sessions must not double-count reinforcements (dedupe by `(claimId, runId)`).
- **N-8 (Trust boundary).** All durable writes pass through Titania's commit gate. Puck (and the agents within it) have no direct write access to any store. No proposal is committed without resolvable grounding and satisfied invariants; the gate is deterministic plain code with no model in the loop.

---

## 13. Risks and open questions

- **Cold-start.** When memory is empty or thin, B has nothing to pull and every claim reads as novel — correct, but not yet valuable. B's contribution grows as the store fills; early operation is effectively A's extraction pass with a quiet B. *Acceptable, but worth signposting in evaluation.*
- **Does dialogue beat a single good pass?** Genuinely uncertain and must be tested rather than assumed (§14). Prior: symmetric dialogue likely loses to a single well-prompted pass once cost is accounted for; the asymmetric variant likely wins specifically on cross-session synthesis and contradiction-catching. The confabulation rate is the number to watch hardest.
- **Manifest growth.** The manifest must be summarised/tiered or it becomes a second unbounded store.
- **Entity-resolution errors.** A wrong alias merge corrupts continuity reasoning. Resolution confidence should itself be tracked.
- **Multi-subject memory.** v0.1 assumes a single subject; namespacing entities and memories per subject is deferred.

---

## 14. Evaluation

Build the harness before the dialogue cleverness — it gates everything.

- **E-1 — Retrieval-miss harness (build first).** Seed memory with known facts, paraphrase them aggressively, and measure how often B fails to retrieve what is provably present. This **false-novelty rate is the ceiling on the whole design**: if it is high, no quality of arguing redeems the system; if it is low, most of the rest is tuning.
- **E-2 — Grounding / confabulation rate.** Fraction of committed memories whose grounding resolves to real source text. Target: 100%; any miss is a defect.
- **E-3 — Comparative ablation.** Run identical session sets through (a) a single well-prompted model, (b) two symmetric models conversing, (c) the asymmetric today-vs-memory setup. Score each on: claim coverage, fraction of claims traceable to real text, invention rate, contradiction-catch rate, and cost. Decide the design empirically.
- **E-4 — Drift over time.** Run consolidation repeatedly over weeks of synthetic sessions; track semantic-store size, duplicate rate, and whether re-summarisation introduces fact drift versus the episodic ground truth.

---

*End of document.*
