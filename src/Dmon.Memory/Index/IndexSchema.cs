using Dmon.Memory.Embedding;

namespace Dmon.Memory.Index;

/// <summary>
/// SQL DDL and query constants for the three-table hybrid index in <c>index.db</c>.
///
/// Table layout (three tables share the same rowid):
/// <list type="bullet">
///   <item><c>memory_content</c> — canonical text + metadata (rowid INTEGER PRIMARY KEY)</item>
///   <item><c>memory_vec</c>     — vec0 virtual table, 768-dim cosine, keyed by rowid</item>
///   <item><c>memory_fts</c>     — FTS5 virtual table, trigram tokenizer, keyed by rowid</item>
///   <item><c>memory_meta</c>    — single-row model/dimension pin for rebuild-on-mismatch</item>
/// </list>
/// </summary>
internal static class IndexSchema
{
    // ── DDL ─────────────────────────────────────────────────────────────────

    internal const string CreateContentTable = """
        CREATE TABLE IF NOT EXISTS memory_content (
            rowid     INTEGER PRIMARY KEY,
            entry_id  TEXT    NOT NULL,
            role      TEXT    NOT NULL,
            text      TEXT    NOT NULL,
            timestamp TEXT    NOT NULL,
            scope     INTEGER NOT NULL
        )
        """;

    // distance_metric=cosine: vec0 uses cosine distance for MATCH queries.
    // The column definition follows sqlite-vec syntax: float[<dim>] distance_metric=<metric>.
    internal static string CreateVecTable =>
        $"""
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_vec
        USING vec0(
            embedding float[{NomicEmbedding.Dimensions}] distance_metric=cosine
        )
        """;

    // trigram tokenizer: indexes every 3-character substring so that partial
    // identifiers, error codes, and file paths are recallable without word boundaries.
    internal const string CreateFtsTable = """
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts
        USING fts5(
            text,
            content='memory_content',
            content_rowid='rowid',
            tokenize='trigram'
        )
        """;

    // Stores the embedding model id and dimension pinned at index creation time.
    // On open, if the stored values differ from the configured model, the index
    // is dropped and rebuilt from JSONL (see IndexConnection.OpenAsync).
    internal const string CreateMetaTable = """
        CREATE TABLE IF NOT EXISTS memory_meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        )
        """;

    internal const string MetaKeyModelId   = "model_id";
    internal const string MetaKeyDimension = "dimension";

    // ── DML ─────────────────────────────────────────────────────────────────

    // Append-only insert — memory_content is never updated in place.
    // RETURNING captures the auto-assigned rowid for vec/FTS linking.
    internal const string InsertContent = """
        INSERT INTO memory_content (entry_id, role, text, timestamp, scope)
        VALUES (@entryId, @role, @text, @timestamp, @scope)
        RETURNING rowid
        """;

    // vec0 upsert: insert or replace using the rowid returned from memory_content.
    internal const string UpsertVec = """
        INSERT OR REPLACE INTO memory_vec (rowid, embedding)
        VALUES (@rowid, @embedding)
        """;

    // FTS5 content-table sync: delete stale shadow row then re-insert.
    internal const string DeleteFts = """
        INSERT INTO memory_fts (memory_fts, rowid, text)
        VALUES ('delete', @rowid, @text)
        """;

    internal const string InsertFts = """
        INSERT INTO memory_fts (rowid, text)
        VALUES (@rowid, @text)
        """;

    // ── Queries ─────────────────────────────────────────────────────────────

    // Hybrid RRF query.
    // Two CTEs — vec KNN and FTS5 MATCH — each row-numbered within their own list.
    // Full-outer-joined on rowid so a result strong in only ONE modality still surfaces.
    // Fused score = vec_weight/(k+vec_rank) + fts_weight/(k+fts_rank).
    // k=60 is the standard RRF constant.
    //
    // Per-modality confidence threshold (D4): applied AFTER the KNN scan, not inside it.
    // sqlite-vec only allows MATCH + k = N in the KNN WHERE clause; filtering on the
    // computed `distance` column inside that WHERE is not supported and throws at runtime.
    // The distance cutoff is enforced in vec_cutoff (outer filter over the KNN result).
    // FTS side: bm25() < @maxFtsBm25Score where bm25() is negative — more negative = better.
    //
    // Scope filter: applied on the final content join so both modalities are restricted.
    //
    // sqlite-vec binds the query vector as a JSON array string (@queryVec).
    // FTS5 bm25() returns a negative score — lower (more negative) = better match.
    //
    // COALESCE handles the full-outer-join: a row missing from one side gets rank=NULL,
    // which contributes 0 to the fused score for that modality.
    internal static string HybridSearch(int limit) => $"""
        WITH vec_knn AS (
            SELECT
                v.rowid,
                v.distance
            FROM memory_vec v
            WHERE v.embedding MATCH @queryVec
              AND k = {limit * 2}
        ),
        vec_cutoff AS (
            SELECT rowid, distance
            FROM vec_knn
            WHERE distance <= @maxVecDistance
        ),
        vec_ranked AS (
            SELECT
                rowid,
                ROW_NUMBER() OVER (ORDER BY distance ASC) AS vec_rank
            FROM vec_cutoff
        ),
        fts_raw AS (
            SELECT
                f.rowid,
                bm25(memory_fts) AS bm25_score
            FROM memory_fts f
            WHERE f.text MATCH @ftsQuery
            LIMIT {limit * 2}
        ),
        fts_cutoff AS (
            SELECT rowid, bm25_score
            FROM fts_raw
            WHERE bm25_score < @maxFtsBm25Score
        ),
        fts_ranked AS (
            SELECT
                rowid,
                ROW_NUMBER() OVER (ORDER BY bm25_score ASC) AS fts_rank
            FROM fts_cutoff
        ),
        fused AS (
            SELECT
                COALESCE(vr.rowid, fr.rowid) AS rowid,
                COALESCE({RrfVecWeight}.0 / (60.0 + vr.vec_rank), 0.0)
              + COALESCE({RrfFtsWeight}.0 / (60.0 + fr.fts_rank), 0.0) AS fused_score
            FROM vec_ranked vr
            FULL OUTER JOIN fts_ranked fr ON vr.rowid = fr.rowid
        )
        SELECT
            c.entry_id,
            c.text,
            c.scope,
            f.fused_score
        FROM fused f
        JOIN memory_content c ON c.rowid = f.rowid
        WHERE c.scope = @scope
        ORDER BY f.fused_score DESC
        LIMIT {limit}
        """;

    // Per-modality weights for RRF fusion. Equal weight; adjust here to tune recall/precision.
    internal const double RrfVecWeight = 1;
    internal const double RrfFtsWeight = 1;

    // Maximum cosine distance considered a plausible match (cosine distance in [0,2];
    // 0 = identical, 2 = opposite). 1.0 excludes clearly opposite vectors.
    internal const double DefaultMaxVecDistance = 1.0;

    // FTS5 bm25() returns a negative number — lower (more negative) = better match.
    // Permissive default: 0.0 admits any FTS match (score is always ≤ 0 for a match).
    // Lower this (e.g. -0.5) to require a stronger keyword match before fusing.
    internal const double DefaultMaxFtsBm25Score = 0.0;

    // ── Drop (for rebuild) ───────────────────────────────────────────────────

    internal const string DropVecTable     = "DROP TABLE IF EXISTS memory_vec";
    internal const string DropFtsTable     = "DROP TABLE IF EXISTS memory_fts";
    internal const string DropContentTable = "DROP TABLE IF EXISTS memory_content";
    internal const string DropMetaTable    = "DROP TABLE IF EXISTS memory_meta";

    internal const string ReadMetaValue = "SELECT value FROM memory_meta WHERE key = @key";

    internal const string UpsertMeta = """
        INSERT INTO memory_meta (key, value) VALUES (@key, @value)
        ON CONFLICT(key) DO UPDATE SET value = excluded.value
        """;

}
