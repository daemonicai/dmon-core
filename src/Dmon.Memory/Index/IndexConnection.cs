using System.Text;
using System.Text.Json;
using Dmon.Memory.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory.Index;

/// <summary>
/// Owns the lifetime of the per-session <c>index.db</c> SQLite connection.
/// Responsibilities:
/// <list type="bullet">
///   <item>Open the connection, enable extension loading, load vec0.</item>
///   <item>Create the three-table schema (content, vec0, FTS5, meta) on first open.</item>
///   <item>Validate the pinned model/dimension against the configured values; rebuild
///         the index from JSONL if they differ (task 3.4).</item>
/// </list>
/// Dispose to close the underlying connection.
/// </summary>
internal sealed class IndexConnection : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    private IndexConnection(SqliteConnection connection)
    {
        _connection = connection;
    }

    internal SqliteConnection Connection => _connection;

    /// <summary>
    /// Opens <c>index.db</c> in <paramref name="sessionDir"/>, loads the vec0 extension,
    /// creates schema, and validates the model pin.  When the stored model/dimension differs
    /// from <paramref name="modelId"/>/<paramref name="dimensions"/>, the index is cleared and
    /// rebuilt by calling <paramref name="rebuildFromJsonl"/>.
    /// </summary>
    internal static async Task<IndexConnection> OpenAsync(
        string sessionDir,
        string modelId,
        int dimensions,
        Func<IndexConnection, CancellationToken, Task> rebuildFromJsonl,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        string dbPath = Path.Combine(sessionDir, "index.db");
        // Pooling=false: each IndexConnection owns its physical connection exclusively.
        // Pooled connections would not have vec0 loaded, causing "malformed" errors when
        // vec0 shadow tables are accessed on a reused connection that skipped LoadVec0.
        // The principled alternative is to re-load vec0 on every physical connection open
        // (e.g. via a SqliteConnection StateChange/Open hook); Pooling=false is acceptable
        // here because ShortTermMemory opens one IndexConnection at initialization and holds
        // it for the lifetime of the session — there is no reuse across calls.
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        SqliteConnection connection = new(connectionString);
        connection.Open();

        // Load vec0 before any DDL — extension must be present for the virtual table DDL.
        SqliteVecLoader.LoadVec0(connection);

        IndexConnection idx = new(connection);
        idx.CreateSchema();

        bool needsRebuild = await idx.ValidateOrUpdateMetaAsync(
            modelId, dimensions, cancellationToken).ConfigureAwait(false);

        if (needsRebuild)
        {
            logger?.LogWarning(
                "index.db model pin mismatch in '{SessionDir}' — dropping and rebuilding index.",
                sessionDir);

            idx.DropAllTables();
            idx.CreateSchema();
            await idx.WriteMetaAsync(modelId, dimensions, cancellationToken).ConfigureAwait(false);
            await rebuildFromJsonl(idx, cancellationToken).ConfigureAwait(false);
        }

        return idx;
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    private void CreateSchema()
    {
        ExecuteNonQuery(IndexSchema.CreateContentTable);
        ExecuteNonQuery(IndexSchema.CreateVecTable);
        ExecuteNonQuery(IndexSchema.CreateFtsTable);
        ExecuteNonQuery(IndexSchema.CreateMetaTable);
    }

    private void DropAllTables()
    {
        // Drop in reverse dependency order: FTS and vec before content, meta last.
        ExecuteNonQuery(IndexSchema.DropFtsTable);
        ExecuteNonQuery(IndexSchema.DropVecTable);
        ExecuteNonQuery(IndexSchema.DropContentTable);
        ExecuteNonQuery(IndexSchema.DropMetaTable);
    }

    // ── Meta / pin ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when the stored pin differs from <paramref name="modelId"/>
    /// and <paramref name="dimensions"/> — caller must rebuild.
    /// On a fresh (empty meta) db, writes the pin and returns <see langword="false"/>.
    /// </summary>
    private async Task<bool> ValidateOrUpdateMetaAsync(
        string modelId,
        int dimensions,
        CancellationToken cancellationToken)
    {
        string? storedModel = await ReadMetaAsync(IndexSchema.MetaKeyModelId, cancellationToken)
            .ConfigureAwait(false);
        string? storedDim = await ReadMetaAsync(IndexSchema.MetaKeyDimension, cancellationToken)
            .ConfigureAwait(false);

        if (storedModel is null && storedDim is null)
        {
            // Fresh db — write pin, no rebuild needed.
            await WriteMetaAsync(modelId, dimensions, cancellationToken).ConfigureAwait(false);
            return false;
        }

        bool mismatch =
            storedModel != modelId ||
            storedDim != dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return mismatch;
    }

    private async Task<string?> ReadMetaAsync(string key, CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = IndexSchema.ReadMetaValue;
        cmd.Parameters.AddWithValue("@key", key);

        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DBNull or null ? null : (string)result;
    }

    internal async Task WriteMetaAsync(string modelId, int dimensions, CancellationToken cancellationToken)
    {
        await UpsertMetaAsync(IndexSchema.MetaKeyModelId, modelId, cancellationToken).ConfigureAwait(false);
        await UpsertMetaAsync(
            IndexSchema.MetaKeyDimension,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertMetaAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = IndexSchema.UpsertMeta;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Upsert (one transaction for content + vec + FTS) ─────────────────────

    /// <summary>
    /// Upserts a batch of <paramref name="entries"/> in a single SQLite transaction.
    /// Per task 5.1 (D8): application-level upsert, no SQLite triggers.
    /// Transaction ordering: content row inserted first (to get rowid), then vec, then FTS.
    /// A failure rolls back the entire batch — the index is never partially written for
    /// that batch (the JSONL lines are already durable and recoverable via rebuild).
    /// </summary>
    internal async Task UpsertBatchAsync(
        IReadOnlyList<IndexEntry> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;

        await using SqliteTransaction tx = _connection.BeginTransaction();
        try
        {
            foreach (IndexEntry entry in entries)
            {
                long rowid = await InsertContentAsync(entry, tx, cancellationToken)
                    .ConfigureAwait(false);

                await InsertVecAsync(rowid, entry.Embedding, tx, cancellationToken)
                    .ConfigureAwait(false);

                await UpsertFtsAsync(rowid, entry.Text, tx, cancellationToken)
                    .ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<long> InsertContentAsync(
        IndexEntry entry,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IndexSchema.InsertContent;
        cmd.Parameters.AddWithValue("@entryId",   entry.EntryId);
        cmd.Parameters.AddWithValue("@role",      entry.Role);
        cmd.Parameters.AddWithValue("@text",      entry.Text);
        cmd.Parameters.AddWithValue("@timestamp", entry.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@scope",     (int)entry.Scope);

        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l ? l : Convert.ToInt64(result);
    }

    private async Task InsertVecAsync(
        long rowid,
        float[] embedding,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        // sqlite-vec accepts the query vector as a JSON array string.
        string vecJson = ToJsonArray(embedding);

        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = IndexSchema.UpsertVec;
        cmd.Parameters.AddWithValue("@rowid",     rowid);
        cmd.Parameters.AddWithValue("@embedding", vecJson);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpsertFtsAsync(
        long rowid,
        string text,
        SqliteTransaction tx,
        CancellationToken cancellationToken)
    {
        // memory_content is append-only (never updated or deleted in place), so each
        // rowid is unique and there is never a stale FTS shadow row to remove.
        // Omitting the FTS5 'delete' command is safe: no row is ever re-inserted under
        // the same rowid, so no stale FTS shadow row can exist.
        // (The 'delete' command also triggers an internal read from memory_content
        // while vec0 shadow-table writes are pending in the same transaction, which
        // has been observed to cause errors; the append-only invariant makes it
        // unnecessary regardless.)
        await using SqliteCommand insCmd = _connection.CreateCommand();
        insCmd.Transaction = tx;
        insCmd.CommandText = IndexSchema.InsertFts;
        insCmd.Parameters.AddWithValue("@rowid", rowid);
        insCmd.Parameters.AddWithValue("@text",  text);
        await insCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of rows in <c>memory_content</c>.
    /// Used by <see cref="ShortTermMemory.InitializeAsync"/> to detect an empty index
    /// after <c>index.db</c> was deleted, which requires a JSONL rebuild.
    /// </summary>
    internal async Task<int> CountContentRowsAsync(CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM memory_content";
        object? result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l ? (int)l : Convert.ToInt32(result);
    }

    // ── Hybrid search ────────────────────────────────────────────────────────

    internal async Task<IReadOnlyList<SearchRow>> HybridSearchAsync(
        float[] queryEmbedding,
        string ftsQuery,
        int limit,
        double maxVecDistance,
        int scope,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = IndexSchema.HybridSearch(limit);
        cmd.Parameters.AddWithValue("@queryVec",         ToJsonArray(queryEmbedding));
        cmd.Parameters.AddWithValue("@maxVecDistance",   maxVecDistance);
        cmd.Parameters.AddWithValue("@ftsQuery",         ftsQuery);
        cmd.Parameters.AddWithValue("@maxFtsBm25Score",  IndexSchema.DefaultMaxFtsBm25Score);
        cmd.Parameters.AddWithValue("@scope",            scope);

        List<SearchRow> rows = [];
        await using SqliteDataReader reader =
            await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SearchRow(
                EntryId:    reader.GetString(0),
                Text:       reader.GetString(1),
                Scope:      reader.GetInt32(2),
                FusedScore: reader.GetDouble(3)));
        }

        return rows;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void ExecuteNonQuery(string sql)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Serialises a float array to a compact JSON array string, e.g. <c>[0.5,0.1,...]</c>.
    /// sqlite-vec accepts this form for MATCH and INSERT bindings.
    /// </summary>
    private static string ToJsonArray(float[] vector)
    {
        StringBuilder sb = new(vector.Length * 12);
        sb.Append('[');
        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append(vector[i].ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _connection.Dispose();
    }
}

/// <summary>
/// A single entry ready to be written into the three-table hybrid index.
/// </summary>
internal sealed record IndexEntry(
    string         EntryId,
    string         Role,
    string         Text,
    DateTimeOffset Timestamp,
    Dmon.Abstractions.Memory.MemoryScope Scope,
    float[]        Embedding
);

/// <summary>
/// A row returned by the hybrid RRF search query.
/// </summary>
internal sealed record SearchRow(
    string EntryId,
    string Text,
    int    Scope,
    double FusedScore
);
