> **Before starting:** confirm the `Dmon.Abstractions.Memory` contracts from `add-memory-abstraction` are present (see proposal prerequisite). This change implements against them and MUST NOT redefine them.

## 1. Project setup

- [x] 1.1 Confirm the home-project decision (D7): create a new `Dmon.Memory` project referencing `Dmon.Abstractions` + `Dmon.Core` (recommended), or extend `Dmon.Core/Session`. Add the project (and a `test/Dmon.Memory.Tests` project) to `Dmon.slnx` and the `Makefile`; verify `make build` picks them up.

## 2. Local embedding generator

- [x] 2.1 Add `LlamaSharp` + `LLamaSharp.Backend.Cpu`; choose and pin the GGUF model + dimension (decide the model — see design Open Questions)
- [x] 2.2 Implement a long-lived singleton `IEmbeddingGenerator<string, Embedding<float>>` over `LLamaEmbedder` (`PoolingType.Mean`, `Embeddings = true`)
- [x] 2.3 L2-normalize all output vectors (LlamaSharp does not normalize by default)
- [x] 2.4 Apply nomic task prefixes at the call sites (`search_document:` on store, `search_query:` on query) — above the generator, not inside it
- [x] 2.5 Serialize/pool embedder access (the context is not thread-safe); add a batched ingest path
- [ ] 2.6 First-run model acquisition/caching [needs human verification — first-run download + cache cannot be settled by automated gates]

## 3. Short-term hybrid index (index.db)

- [ ] 3.1 Add `Microsoft.Data.Sqlite` (`SQLitePCLRaw.bundle_e_sqlite3`) and bundle the `sqlite-vec` native loadable per-RID
- [ ] 3.2 Open per-session `index.db`, enable extension loading + `LoadExtension("vec0")`; fail fast with a clear message if the loadable cannot be loaded
- [ ] 3.3 Create schema: content table, `vec0` virtual table (cosine), FTS5 virtual table (`trigram` tokenizer for code)
- [ ] 3.4 Pin embedding model id + dimension in the db; implement rebuild-on-mismatch
- [ ] 3.5 Implement the single-query weighted-RRF hybrid search (`vec0` KNN CTE + FTS5 `MATCH` CTE, full-outer-join on rowid, `1/(k+rank)` with per-modality weights; threshold per-list before fusing)
- [ ] 3.6 Implement rebuild-from-JSONL (derived projection)
- [ ] 3.7 Wire `IShortTermMemory` over the canonical JSONL + `index.db`: `RecordAsync` appends JSONL + embeds + upserts content/`vec0`/FTS5 in one transaction; `FlushAsync` completes pending indexing; verbatim `ReadMessagesAsync` is served from the JSONL; hits carry `Source = ShortTerm` and null `Relations`

## 4. Tests

- [ ] 4.1 Embedding: L2-normalization (unit norm), prefix application (document vs. query), determinism
- [ ] 4.2 Short-term hybrid: single-modality recall, no-match → empty list, rebuild-from-JSONL parity, model/dimension-mismatch rebuild
- [ ] 4.3 Flush barrier: recorded turns are searchable after `FlushAsync`
- [ ] 4.4 Record path: transactional consistency (a failed mid-ingest leaves the index consistent); verbatim `ReadMessagesAsync` matches the canonical JSONL

## 5. Assumed defaults / decisions to confirm

- [ ] 5.1 Proceed with an application-level upsert in one transaction for `index.db` (content + `vec0` + FTS5); revisit SQLite triggers only if it bottlenecks. (Local-only — settled.)
- [x] 5.2 Decide the GGUF embedding model + dimension (and whether to use nomic v1.5 Matryoshka truncation to shrink `index.db`). (Local choice — no external dependency.)
- [ ] 5.3 Confirm and document the supported RID matrix for the `sqlite-vec` loadable + LlamaSharp CPU backend, and how the loadable is resolved at runtime. (Local choice.)
