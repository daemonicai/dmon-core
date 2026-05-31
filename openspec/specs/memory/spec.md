# memory Specification

## Purpose
The dmon memory capability: the agent's recall over conversation and durable knowledge. This spec currently defines the **short-term tier** — a local embedding generator and a per-session hybrid (keyword + vector) search index (`index.db`) derived from the canonical `messages.jsonl`, giving semantic recall over a session without changing the files-and-directories storage contract. The long-term (Meko) tier and the cross-tier `IMemory` facade are specified and built by separate changes and will extend this capability.
## Requirements
### Requirement: Local embedding generator

The system SHALL provide a local embedding generator implementing `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`, backed by a local GGUF model via `LlamaSharp` (CPU backend), configured with mean pooling and embedding mode. The generator SHALL be a single long-lived instance whose access is serialized (the underlying context is not thread-safe) and SHALL support batched input on ingest. Every output vector SHALL be L2-normalized before it is returned.

#### Scenario: Output vectors are unit-normalized
- **WHEN** the generator produces an embedding for any input
- **THEN** the returned vector has an L2 norm of 1 (within floating-point tolerance)

#### Scenario: Deterministic embeddings for identical input
- **WHEN** the same input string is embedded twice with the same model
- **THEN** the two vectors are equal (within floating-point tolerance)

#### Scenario: Concurrent callers are serialized
- **WHEN** multiple callers request embeddings concurrently
- **THEN** access to the underlying embedder is serialized and no call corrupts another's result

### Requirement: Task-prefixed embedding at the call sites

Stored text SHALL be embedded with the document task-prefix (`search_document:`) and search queries SHALL be embedded with the query task-prefix (`search_query:`). The prefix SHALL be applied above the embedding generator at the call site (store vs. query), not inside the generator, so the generator remains prefix-agnostic.

#### Scenario: Stored content uses the document prefix
- **WHEN** session content is embedded for storage in the index
- **THEN** it is embedded with the `search_document:` task-prefix

#### Scenario: Queries use the query prefix
- **WHEN** a search query is embedded
- **THEN** it is embedded with the `search_query:` task-prefix

### Requirement: Short-term memory preserves files and adds a derived semantic index

`IShortTermMemory` SHALL retain the directories-and-files model of `ISessionStore` (canonical per-session JSONL as the source of truth) and SHALL add a per-session SQLite `index.db` providing hybrid semantic search. The `index.db` SHALL be a derived, rebuildable projection of the JSONL: deleting it and replaying the JSONL SHALL reproduce the searchable state. The `index.db` SHALL pin the embedding model identifier and vector dimension, and on a mismatch SHALL rebuild rather than mix vector spaces.

#### Scenario: Index is rebuildable from canonical storage
- **WHEN** `index.db` is deleted and the session is re-opened
- **THEN** the index is rebuilt from the JSONL with no loss of recallable content

#### Scenario: Embedding model change triggers rebuild
- **WHEN** the configured embedding model or dimension differs from the values pinned in an existing `index.db`
- **THEN** the index is rebuilt; vectors from different models are never mixed in the same index

#### Scenario: Verbatim reads remain available
- **WHEN** a caller requests chronological/verbatim session content
- **THEN** `IShortTermMemory` serves it from the canonical JSONL (the semantic index does not replace verbatim reads)

### Requirement: Short-term search is hybrid keyword + vector with rank-based fusion

`IShortTermMemory.SearchAsync` SHALL combine a vector (KNN) result and a full-text (FTS5) result over the session `index.db` and fuse them using Reciprocal Rank Fusion. Fusion SHALL operate on rank position, not on raw scores. The FTS5 table SHALL use a tokenizer suited to code-bearing text (e.g. `trigram`) so that identifiers, paths, and error codes are recallable. Stored text SHALL be embedded with the document task-prefix and queries with the query task-prefix, and all vectors SHALL be L2-normalized before storage and comparison.

#### Scenario: A result strong in only one modality still surfaces
- **WHEN** an entry is a top vector match but a weak keyword match (or vice versa)
- **THEN** RRF includes it in the fused ranking rather than discarding it

#### Scenario: Cross-modality scores are never directly compared
- **WHEN** vector distances and FTS5 BM25 scores are combined
- **THEN** the fusion uses each result's rank within its own list, not the raw distance/BM25 magnitude

#### Scenario: No matches
- **WHEN** neither index returns a candidate for the query
- **THEN** `SearchAsync` returns an empty list

#### Scenario: Results are attributed to short-term
- **WHEN** `SearchAsync` returns hits
- **THEN** each `MemoryHit` has `Source = ShortTerm` and a null/empty `Relations` (the keyword+vector index has no graph)

### Requirement: Short-term record path is transactional

`IShortTermMemory.RecordAsync` SHALL append the supplied turns to the canonical JSONL and update the index's content, vector, and full-text tables for that content within a single transaction, so that a failed ingest never leaves `index.db` partially written relative to a recorded turn.

#### Scenario: Failed ingest does not corrupt the index
- **WHEN** indexing fails partway through recording a turn
- **THEN** the index is left in a consistent state (the partial vector/FTS/content rows for that turn are not committed)

### Requirement: Short-term consistency and flush barrier

Short-term memory SHALL be read-your-writes within a session, subject only to indexing latency. `FlushAsync` SHALL act as a materialization barrier that completes any pending indexing, after which recorded turns are searchable. `FlushAsync` SHALL return `ValueTask`.

#### Scenario: Flush makes short-term writes searchable
- **WHEN** turns are recorded and `FlushAsync()` then completes
- **THEN** those turns are returned by `SearchAsync` for a query that matches them

#### Scenario: Recently recorded content is recallable in-session
- **WHEN** content has been recorded earlier in the same session and indexing has completed (or `FlushAsync` has run)
- **THEN** a semantically matching query recalls it via `SearchAsync`

### Requirement: Memory facade fans out writes and fuses reads

The system SHALL provide an `IMemory` facade (`Memory`) exposing read-only `ShortTerm` (`IShortTermMemory`, never null) and `LongTerm` (`ILongTermMemory`, null when long-term is not configured). `IMemory.RecordAsync` SHALL dispatch to `ShortTerm.RecordAsync` always and to `LongTerm.RecordAsync` when long-term is configured. `IMemory.FlushAsync` SHALL flush every configured tier. `IMemory.SearchAsync` SHALL return results fused from both tiers.

#### Scenario: Record reaches both tiers
- **WHEN** `IMemory.RecordAsync(turns)` is called and long-term is configured
- **THEN** the turns are passed to both `ShortTerm.RecordAsync` and `LongTerm.RecordAsync` (each tier then applies its own policy)

#### Scenario: Explicit single-tier access
- **WHEN** a caller needs exactly one tier
- **THEN** it uses `memory.ShortTerm` or `memory.LongTerm` directly, bypassing fusion

#### Scenario: Flush spans configured tiers
- **WHEN** `IMemory.FlushAsync()` completes
- **THEN** `ShortTerm.FlushAsync` has completed and, if configured, `LongTerm.FlushAsync` has been invoked

### Requirement: Cross-tier read fusion is rank-based with provenance

`IMemory.SearchAsync` SHALL query both configured tiers and fuse their result lists using Reciprocal Rank Fusion over each result's rank within its own tier — raw cross-tier `Score` values SHALL NOT be compared directly. Each returned `MemoryHit` SHALL retain its originating `Source`. The facade SHALL de-duplicate near-identical hits that surface from both tiers, keeping a single fused entry.

#### Scenario: Results are attributable to a tier
- **WHEN** `IMemory.SearchAsync` returns fused results
- **THEN** each `MemoryHit.Source` indicates whether it came from short-term or long-term

#### Scenario: Cross-tier scores are fused by rank, not magnitude
- **WHEN** short-term and long-term both return candidates
- **THEN** the fused order is computed from each hit's rank within its own tier (RRF), not from the raw `Score` values (which come from different models)

#### Scenario: A duplicate fact from both tiers appears once
- **WHEN** the same fact is returned by both short-term and long-term
- **THEN** the fused result contains a single de-duplicated entry rather than two near-identical hits

#### Scenario: One tier empty
- **WHEN** only one tier returns candidates (the other is empty)
- **THEN** `SearchAsync` returns that tier's results, fused/ranked, without error

### Requirement: Long-term tier is optional and decoupled

The facade SHALL function with long-term memory absent: when no `ILongTermMemory` is configured, `LongTerm` is null, writes go only to short-term, `SearchAsync` returns short-term results only, and no long-term operation is attempted. The short-term/facade assembly SHALL NOT depend on any specific long-term implementation package; the long-term tier SHALL be supplied independently through dependency injection.

#### Scenario: Short-term-only operation
- **WHEN** the facade is configured without a long-term tier
- **THEN** record/search/flush operate on short-term alone and `LongTerm` is null

#### Scenario: Long-term supplied independently is picked up
- **WHEN** a long-term implementation is registered separately in the container
- **THEN** the facade resolves it and fans out / fuses across both tiers — without the core assembly referencing that implementation's package

#### Scenario: A failing long-term tier degrades, not breaks
- **WHEN** a configured long-term search fails or times out
- **THEN** `IMemory.SearchAsync` still returns the short-term results (the failure is contained, not propagated)

### Requirement: Memory composition via dependency injection

The system SHALL provide an `AddDmonMemory()` `IServiceCollection` extension that registers the local embedding generator, the short-term memory store, and the `IMemory` facade. The facade registration SHALL consume an `ILongTermMemory` from the container if one is present and otherwise wire long-term as absent.

#### Scenario: One call wires the short-term-capable facade
- **WHEN** a host calls `AddDmonMemory()`
- **THEN** `IMemory` resolves to a facade backed by a working short-term tier (embedder + index)

#### Scenario: Adding long-term is a second, independent call
- **WHEN** a host calls `AddDmonMemory()` and also registers a long-term tier (e.g. via the Meko package's own extension)
- **THEN** the resolved `IMemory` fuses both tiers, regardless of the order the two registrations were made

