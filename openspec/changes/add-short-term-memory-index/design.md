## Context

dmon-core is a .NET 10 agentic coding harness (nullable enabled, warnings-as-errors). It already has:
- `ISessionStore` (`src/Dmon.Core/Session/SessionStore.cs`) — file/directory storage, `messages.jsonl` + `meta.json`, atomic `.tmp`-rename writes, `IReadOnlyList<object>` reads via `ReadMessagesAsync(sessionId, applyCompaction, ct)`. This is the canonical short-term storage.
- `Microsoft.Extensions.AI` (10.6.0) for AI abstractions (ADR-001) — including `IEmbeddingGenerator<TInput, TEmbedding>` and `Embedding<float>`.
- The memory contracts from `add-memory-abstraction` (in `Dmon.Abstractions.Memory`): `IShortTermMemory : IMemoryStore`, with `RecordAsync(IReadOnlyList<ChatMessage> turns, MemoryScope scope = Agent, CancellationToken)`, `SearchAsync(string query, MemoryScope scope = Agent, int limit = 10, CancellationToken)` → `IReadOnlyList<MemoryHit>`, `FlushAsync(CancellationToken)` → `ValueTask`, plus the verbatim carry-over `ReadMessagesAsync(bool applyCompaction = true, CancellationToken)` → `IReadOnlyList<object>`. `MemoryHit { Id, Text, Source, Score, Metadata? (IReadOnlyDictionary<string, JsonElement>?), Relations? }`; `Relations` is long-term-only and null for short-term. `MemoryScope { Agent, Session, User, Shared }` (default `Agent`).

This change implements the short-term tier. It is modelled on **memlite** (a hybrid FTS5 + `sqlite-vec` engine) for the index and on nomic-style embeddings for the vectors. The long-term (Meko) tier and the cross-tier facade are deliberately not built here.

## Goals / Non-Goals

**Goals:**
- A local, offline-capable embedding generator wrapped as `IEmbeddingGenerator<string, Embedding<float>>`.
- A per-session, rebuildable `index.db` giving hybrid (vector + keyword) semantic search over the canonical JSONL.
- An `IShortTermMemory` implementation that keeps the files-and-dirs model intact and adds the index alongside it.
- An honest short-term consistency contract: read-your-writes within a session (subject to indexing latency), with `FlushAsync` as the materialization barrier.

**Non-Goals:**
- The `IMemory` facade, cross-tier RRF fusion, and `AddDmonMemory()` DI wiring (a later change).
- The Meko-backed `ILongTermMemory` (built in the `dmon-meko` repo).
- Any change to the canonical session-storage contract (append-only `messages.jsonl`, `attachments/`).
- GPU/Metal embedding backends beyond what `LLamaSharp.Backend.Cpu` provides by default.

## Decisions

### D2. Short-term = canonical JSONL + per-session `index.db`
Canonical storage stays the JSONL files. A sibling `index.db` (SQLite) adds hybrid search and is a **derived, rebuildable projection** — delete it and replay the JSONL to reproduce the searchable state. Pin the embedding model identifier + dimension in the db; on mismatch, **rebuild** rather than mix vector spaces (we can afford rebuild because the index is not the source of truth).
- *Why:* preserves the inspectability/diff-ability of files-and-dirs; makes the index disposable and model-version-safe.

### D3. Hybrid search = raw `sqlite-vec` + FTS5 + RRF (no Semantic Kernel connector)
Use `Microsoft.Data.Sqlite` (`SQLitePCLRaw.bundle_e_sqlite3`, which ships FTS5 and permits `LoadExtension`) + the `sqlite-vec` loadable. Express the hybrid as a **single SQL query**: a `vec0` KNN CTE and an FTS5 `MATCH` CTE, each rank-numbered, `full outer join`ed on a shared rowid, combined with weighted RRF arithmetic in SQL.
- *Why:* `Microsoft.SemanticKernel.Connectors.SqliteVec` is vector-only (no keyword-hybrid), Preview, `EqualTo`-only filtering, and its auto-embed cannot apply nomic's dual prefixes (D5). Going raw gives one owned SQL surface, weighted RRF, full knob control, and transactional consistency.
- *Cost:* one native loadable (`sqlite-vec`) and the `LoadExtension` dance — the same class of native-dependency management already accepted by choosing LlamaSharp.

### D4. RRF fusion is rank-based, never score-based
BM25 (keyword) and cosine distance (vector) live in incomparable spaces. Fuse by **rank** with `1/(k+rank)` (conventionally `k=60`), with per-modality weights. Apply any confidence threshold *before* fusing, as a per-list cutoff. A result strong in only one modality must still surface (full-outer-join, not inner).

### D5. Local embeddings via LlamaSharp wrapped as `IEmbeddingGenerator<string, Embedding<float>>`
A ~100 MB nomic-like GGUF, `PoolingType.Mean`, `Embeddings = true`, `LLamaSharp.Backend.Cpu` (CPU on all platforms; Metal on macOS).
- **L2-normalize** every output vector — LlamaSharp does **not** normalize by default.
- **nomic dual prefixes** — embed stored text as `search_document: …` and queries as `search_query: …`. Because the prefix is call-site-dependent and `IEmbeddingGenerator` is prefix-blind, the prefix logic lives *above* the generator at the two call sites (store vs. query).
- One long-lived **singleton** embedder; **serialize/pool** access (the `LLamaEmbedder` context is not thread-safe); **batch** on ingest.

### D6. FTS5 tokenizer tuned for code
The default `unicode61` tokenizer fragments identifiers / paths / error codes. Use the `trigram` tokenizer (or a custom one) on code-bearing fields — keyword recall on identifiers is the whole reason FTS5 is in the mix.

### D7. Home project for the short-term implementation (confirm during group 1)
The parent design said "evolve `Dmon.Core/Session`," but that would pull two native loadables (`sqlite-vec`, `llama.cpp`) directly into `Dmon.Core`, the agent core. **Recommended:** create a new project **`Dmon.Memory`** (referencing `Dmon.Abstractions` + `Dmon.Core`) that houses the embedder, the index, and the `IShortTermMemory` implementation, keeping the native dependencies isolated and swappable. Add it to `Dmon.slnx` and the `Makefile`. The implementer should confirm this placement (vs. extending `Dmon.Core`) as the first task; either is acceptable, but isolating the native footprint is preferred. A matching test project (`Dmon.Memory.Tests`) follows the existing `test/` convention.

### D8. Index write path is one transaction (assumed default 7.3, settled)
On record, the three tables (content + `vec0` + FTS5) are kept consistent via an **application-level upsert in a single transaction**. Revisit SQLite triggers only if this bottlenecks. A failed ingest must not leave `index.db` half-written.

## Risks / Trade-offs

- **Per-turn embedding latency/cost (CPU)** → blocking the turn loop. Mitigation: singleton embedder, batched ingest, async indexing; accept a small "written-to-JSONL-but-not-yet-searchable" window closed by `FlushAsync`.
- **Native-dependency footprint** (`sqlite-vec` + `llama.cpp`) → packaging weight, trimming/AOT friction, per-RID payloads. Mitigation: CPU backend by default; document the supported RIDs; keep both behind the new project so they're swappable (D7).
- **Embedding-model swap** → silent vector-space mixing. Mitigation: pin model id + dimension in `index.db`, rebuild on mismatch (D2).
- **`sqlite-vec` loadable resolution** across RIDs / `LoadExtension` availability → runtime load failures. Mitigation: verify the bundle ships the loadable per supported RID; fail fast with a clear message if `LoadExtension("vec0")` fails.
- **First-run model download** (~100 MB) → first session is slow / needs network. Mitigation: cache under a known path; document the behaviour; this step needs human verification (it can't be settled by automated gates).

## Migration Plan

Additive. No existing specs change; the canonical JSONL is untouched, so rollback = delete `index.db` (the index rebuilds from JSONL on next open) and/or remove the `Dmon.Memory` project reference. The short-term store is only wired into the agent once a later change adds the facade + DI; until then it is exercised by its own tests.

Rollout within this change: (1) project setup + native deps; (2) embedding generator; (3) `index.db` schema + hybrid search + rebuild; (4) `IShortTermMemory` wiring over JSONL + index; (5) tests.

## Open Questions

- **[deferred — local choice]** The specific GGUF embedding model + dimension, and whether to use nomic v1.5 Matryoshka truncation to shrink `index.db`. Decide during group 1; no external dependency.
- **[deferred — local choice]** The supported RID matrix for the `sqlite-vec` loadable and the LlamaSharp CPU backend (e.g. `osx-arm64`, `osx-x64`, `linux-x64`, `win-x64`), and how the loadable is bundled/resolved at runtime. Confirm during group 2.
- **[settled]** `index.db` three-table sync mechanism → application-level upsert in one transaction (D8); triggers only if it bottlenecks.
