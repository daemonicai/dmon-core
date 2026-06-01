## MODIFIED Requirements

### Requirement: Short-term memory preserves files and adds a derived semantic index

`IShortTermMemory` SHALL treat the per-session canonical JSONL — **owned and written by session-storage** — as the source of truth, and SHALL add a per-session SQLite `index.db` providing hybrid semantic search. `IShortTermMemory` SHALL NOT write the canonical JSONL. The `index.db` SHALL be a derived, rebuildable projection of the JSONL: deleting it and replaying the JSONL SHALL reproduce the searchable state with no loss of recallable content. The `index.db` SHALL pin the embedding model identifier and vector dimension, and on a mismatch SHALL rebuild rather than mix vector spaces.

#### Scenario: Index is rebuildable from canonical storage
- **WHEN** `index.db` is deleted and the session is re-opened
- **THEN** the index is rebuilt from the canonical JSONL (parsing `message` records and deriving searchable text from their `text` parts) with no loss of recallable content

#### Scenario: Embedding model change triggers rebuild
- **WHEN** the configured embedding model or dimension differs from the values pinned in an existing `index.db`
- **THEN** the index is rebuilt; vectors from different models are never mixed in the same index

#### Scenario: Verbatim reads remain available
- **WHEN** a caller requests chronological/verbatim session content
- **THEN** it is served from the canonical JSONL (the semantic index does not replace verbatim reads)

### Requirement: Short-term record path is transactional

`IShortTermMemory.RecordAsync` SHALL build the index's content, vector, and full-text tables for the supplied turns within a single transaction, so that a failed ingest never leaves `index.db` partially written relative to a recorded turn. `RecordAsync` SHALL NOT append to the canonical JSONL (session-storage owns that write) and SHALL key index rows on the `entryId` supplied with each turn rather than minting its own. A successful return guarantees index consistency only; canonical durability is guaranteed by the prior session-storage append.

#### Scenario: Failed ingest does not corrupt the index
- **WHEN** indexing fails partway through recording a turn
- **THEN** the index is left in a consistent state (the partial vector/FTS/content rows for that turn are not committed)

#### Scenario: Record does not write canonical storage
- **WHEN** `IShortTermMemory.RecordAsync(turns)` is called
- **THEN** it updates only `index.db`; it does not append to `messages.jsonl`, and it indexes under the `entryId` supplied by session-storage
