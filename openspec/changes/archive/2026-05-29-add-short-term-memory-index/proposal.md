> **Prerequisite — the memory abstractions must be present.** This change implements the short-term tier against the memory contracts introduced by the sibling change **`add-memory-abstraction`** (interfaces + DTOs in `Dmon.Abstractions.Memory`: `IMemoryStore`, `IShortTermMemory`, `IMemory`, `MemoryHit`, `MemoryScope`, `MemoryContext`, `MemorySource`, `MemoryRelation`). Those types currently live on branch `change/add-memory-abstraction` (commit `47cad62`), **not** on `main`. Implement this change either on a branch based on `change/add-memory-abstraction`, or after that change is merged to `main`. If the `Dmon.Abstractions.Memory` types are absent, stop — do not redefine them here.
>
> **Provenance.** This change is the dmon-core half of a cross-repo plan. The full memory abstraction was designed in the `dmon-meko` repo's `add-memory-abstraction` change; by agreement the abstractions + short-term tier land in dmon-core while the Meko-backed long-term tier is built in `dmon-meko`. The design decisions below (D-numbers) are carried over from that parent design verbatim where relevant.

## Why

The memory abstraction (`add-memory-abstraction`) defines `IShortTermMemory` but ships no implementation. dmon-core already has `ISessionStore` (`Dmon.Core/Session`) — verbatim, per-session, chronological JSONL storage, i.e. *de facto* short-term memory — but it has no semantic recall: an agent cannot ask "what did we discuss earlier this session about X?" and get back relevant turns by meaning, only by reading the whole log. This change adds the missing piece: a local embedding generator plus a per-session hybrid (keyword + vector) search index that turns the existing canonical JSONL into something semantically searchable, **without** changing the files-and-directories storage contract.

## What Changes

- Add a **local embedding generator**: a long-lived singleton `IEmbeddingGenerator<string, Embedding<float>>` (`Microsoft.Extensions.AI`) over `LlamaSharp`'s `LLamaEmbedder` (CPU backend), with L2-normalized output and nomic dual task-prefixes applied at the call sites.
- Add a **per-session `index.db`** (SQLite via `Microsoft.Data.Sqlite` + the `sqlite-vec` loadable extension + FTS5) providing hybrid search, fused with Reciprocal Rank Fusion (RRF) in a single SQL query. The `index.db` is a **derived, rebuildable projection** of the canonical JSONL — delete it and replay the log to reproduce it.
- Implement **`IShortTermMemory`** over the canonical JSONL (unchanged) plus `index.db`: `RecordAsync` appends to JSONL, embeds, and upserts the index in one transaction; `SearchAsync` runs the hybrid query; `FlushAsync` completes pending indexing (materialization barrier); verbatim/chronological reads are still served from the JSONL.
- Add a home project for this implementation and its native dependencies, wired into `Dmon.slnx` and the `Makefile` (see design **D7**).

**Out of scope (other changes own these):** the `IMemory` facade and cross-tier fusion + `AddDmonMemory()` DI wiring; the Meko-backed `ILongTermMemory` (built in `dmon-meko`); any change to the canonical session-storage contract.

## Capabilities

### New Capabilities

- `memory`: the dmon short-term memory tier — the local embedding generator, the per-session hybrid `index.db`, the `IShortTermMemory` implementation, and the short-term consistency/flush semantics. (The long-term tier and the facade are specified and built elsewhere; this change adds only the short-term requirements to the `memory` capability.)

### Modified Capabilities

<!-- None. The canonical session-storage contract (session-storage capability) is unchanged: index.db is an additive, derived sibling of messages.jsonl, not a modification to how the JSONL itself is stored. -->

## Impact

- **New project / code**: the embedding generator + short-term store implementation (recommended new project `Dmon.Memory`; see D7), referencing `Dmon.Abstractions` and `Dmon.Core`. Added to `Dmon.slnx` and the `Makefile`.
- **New dependencies**: `LlamaSharp` + `LLamaSharp.Backend.Cpu`; `Microsoft.Data.Sqlite` (`SQLitePCLRaw.bundle_e_sqlite3`); the `sqlite-vec` native loadable (per-RID); a local embedding GGUF model (~100 MB, downloaded and cached on first run). `Microsoft.Extensions.AI` is already referenced (ADR-001).
- **Native-dependency footprint**: two native loadables (`sqlite-vec`, `llama.cpp` via LlamaSharp) enter the .NET process — per-RID packaging and (future) trimming/AOT implications. Confine them behind the new project so they stay out of `Dmon.Core`.
- **ADRs**: consistent with ADR-001 (embeddings via `Microsoft.Extensions.AI`'s `IEmbeddingGenerator`; no MAF) and ADR-004 (`messages.jsonl` stays the append-only source of truth; `index.db` is derived and disposable).
