using System.Text;
using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Memory.Embedding;
using Dmon.Protocol.Conversation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory.Index;

/// <summary>
/// Per-session <see cref="IShortTermMemory"/> backed by a three-table hybrid index
/// (<c>vec0</c> + FTS5 + content) in <c>&lt;sessionDir&gt;/index.db</c>.
///
/// Construction does NOT open the database — call <see cref="InitializeAsync"/> first.
/// <see cref="RecordAsync"/> is index-only: it does NOT write to <c>messages.jsonl</c>
/// (session-storage owns that write per ADR-016). It keys index rows on the
/// <c>entryId</c> supplied in each <see cref="MessageRecord"/>.
/// <see cref="FlushAsync"/> is a no-op durability barrier (SQLite WAL checkpoints on close).
/// </summary>
public sealed class ShortTermMemory : IShortTermMemory, IAsyncDisposable
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ISessionStore _sessionStore;
    private readonly ISessionDirectoryResolver _directoryResolver;
    private readonly MemoryContext _context;
    private readonly ILogger<ShortTermMemory>? _logger;

    // Resolved on InitializeAsync; null until then.
    private IndexConnection? _index;
    private string? _sessionDir;

    // Guards writes for this instance (not concurrency-safe per session).
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ShortTermMemory(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ISessionStore sessionStore,
        ISessionDirectoryResolver directoryResolver,
        MemoryContext context,
        ILogger<ShortTermMemory>? logger = null)
    {
        _embeddingGenerator = embeddingGenerator;
        _sessionStore = sessionStore;
        _directoryResolver = directoryResolver;
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Opens (or creates) <c>index.db</c> for the session identified by
    /// <see cref="MemoryContext.ConversationId"/>.  Must be called before any other method.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        string root = _directoryResolver.Resolve(Directory.GetCurrentDirectory());
        _sessionDir = Path.Combine(root, _context.ConversationId);

        _index = await IndexConnection.OpenAsync(
            sessionDir:       _sessionDir,
            modelId:          NomicEmbedding.ModelId,
            dimensions:       NomicEmbedding.Dimensions,
            rebuildFromJsonl: RebuildFromJsonlAsync,
            logger:           _logger,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Recover from a deleted index.db: if the index is empty but messages.jsonl
        // has content, rebuild the index from JSONL so that SearchAsync works correctly.
        // This handles the case where index.db was manually deleted or lost, which
        // OpenAsync treats as a fresh db (no model mismatch) without triggering rebuild.
        string jsonlPath = Path.Combine(_sessionDir, "messages.jsonl");
        if (File.Exists(jsonlPath))
        {
            int contentRows = await _index.CountContentRowsAsync(cancellationToken).ConfigureAwait(false);
            if (contentRows == 0)
            {
                // Check if there is any indexable content in messages.jsonl.
                string[] lines = await File.ReadAllLinesAsync(jsonlPath, cancellationToken).ConfigureAwait(false);
                bool hasIndexableContent = lines.Any(l => !string.IsNullOrWhiteSpace(l));
                if (hasIndexableContent)
                {
                    _logger?.LogInformation(
                        "index.db is empty but messages.jsonl has content — rebuilding index for session '{SessionDir}'.",
                        _sessionDir);
                    await RebuildFromJsonlAsync(_index, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    // ── IShortTermMemory ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<object>> ReadMessagesAsync(
        bool applyCompaction = true,
        CancellationToken cancellationToken = default)
    {
        // Delegated verbatim to ISessionStore (ADR-004: JSONL is the canonical source).
        return await _sessionStore
            .ReadMessagesAsync(_context.ConversationId, applyCompaction, cancellationToken)
            .ConfigureAwait(false);
    }

    // ── IMemoryStore ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Index-only (ADR-016): does NOT write to <c>messages.jsonl</c>; session-storage
    /// owns that write and supplies the <c>entryId</c> on each record.
    ///
    /// Ordering:
    /// 1. Extract index text from each record's <see cref="TextPart"/>s.
    /// 2. Embed all texts in one batch (<c>search_document:</c> prefix).
    /// 3. Upsert content + vec0 + FTS5 in ONE SQLite transaction.
    ///
    /// A failure at step 3 leaves the index inconsistent for those records.
    /// Re-run <c>InitializeAsync</c> (which triggers a rebuild when the model pin
    /// mismatches) or delete <c>index.db</c> to recover.
    /// </remarks>
    public async Task RecordAsync(
        IReadOnlyList<MessageRecord> records,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (records.Count == 0)
            return;

        // Extract indexable text from each record's TextParts before acquiring the lock
        // (embedding is CPU-intensive; keep the lock window minimal).
        List<(string EntryId, string Role, string Text, DateTimeOffset Timestamp)> prepared = [];

        foreach (MessageRecord record in records)
        {
            string text = ExtractText(record);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            prepared.Add((record.EntryId, record.Role, text, record.Timestamp));
        }

        if (prepared.Count == 0)
            return;

        // Embed all texts in one batch (search_document: prefix for storage).
        string[] prefixed = [.. prepared.Select(p => NomicEmbedding.ApplyDocumentPrefix(p.Text))];
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await _embeddingGenerator.GenerateAsync(prefixed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Build index entries from embeddings, keyed on the supplied entryId.
        List<IndexEntry> entries = [];
        for (int i = 0; i < prepared.Count; i++)
        {
            (string entryId, string role, string text, DateTimeOffset timestamp) = prepared[i];
            entries.Add(new IndexEntry(
                EntryId:   entryId,
                Role:      role,
                Text:      text,
                Timestamp: timestamp,
                Scope:     scope,
                Embedding: embeddings[i].Vector.ToArray()));
        }

        // Serialize SQLite writes under one lock.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _index!.UpsertBatchAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Hybrid RRF search: vec0 KNN (cosine) full-outer-joined with FTS5 trigram MATCH,
    /// fused via Reciprocal Rank Fusion (k=60, equal per-modality weights).
    /// The query vector uses the <c>search_query:</c> prefix per nomic task-prefix spec.
    /// Returns an empty list when neither modality matches.
    /// Score is normalized to [0,1] by dividing by the maximum achievable fused score
    /// (weight_vec/(k+1) + weight_fts/(k+1) = 2/61 ≈ 0.0328 for equal weights of 1).
    /// </remarks>
    public async Task<IReadOnlyList<MemoryHit>> SearchAsync(
        string query,
        MemoryScope scope = MemoryScope.Agent,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(query))
            return [];

        // Embed the query with the search_query: prefix.
        GeneratedEmbeddings<Embedding<float>> queryEmbeddings =
            await _embeddingGenerator.GenerateAsync(
                [NomicEmbedding.ApplyQueryPrefix(query)],
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        float[] queryVec = queryEmbeddings[0].Vector.ToArray();

        IReadOnlyList<SearchRow> rows = await _index!.HybridSearchAsync(
            queryEmbedding:  queryVec,
            ftsQuery:        EscapeFtsQuery(query),
            limit:           limit,
            maxVecDistance:  IndexSchema.DefaultMaxVecDistance,
            scope:           (int)scope,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
            return [];

        // Max achievable fused score: both modalities rank the result at position 1.
        double maxFused = (IndexSchema.RrfVecWeight / 61.0) + (IndexSchema.RrfFtsWeight / 61.0);

        List<MemoryHit> hits = [];
        foreach (SearchRow row in rows)
        {
            double normalizedScore = maxFused > 0 ? Math.Min(row.FusedScore / maxFused, 1.0) : 0.0;
            hits.Add(new MemoryHit(
                Id:        row.EntryId,
                Text:      row.Text,
                Source:    MemorySource.ShortTerm,
                Score:     normalizedScore,
                Metadata:  null,
                Relations: null));
        }

        return hits;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Inline indexing design: JSONL is durable on each <see cref="RecordAsync"/> return,
    /// and the SQLite upsert completes before <see cref="RecordAsync"/> returns.
    /// <see cref="FlushAsync"/> is therefore a no-op durability barrier — all writes are
    /// already materialized and searchable.
    /// </remarks>
    public ValueTask FlushAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    // ── Rebuild from JSONL (tasks 3.4 + 3.6) ────────────────────────────────

    /// <summary>
    /// Reads the raw <c>messages.jsonl</c> lines for the session (verbatim, NOT
    /// compaction-collapsed — we index the original content), extracts indexable
    /// text from each line, re-embeds in batches, and repopulates all three tables.
    ///
    /// Called by <see cref="IndexConnection.OpenAsync"/> when model/dimension mismatch
    /// is detected, and may be called directly to force a rebuild (e.g. after deleting
    /// <c>index.db</c>).
    /// </summary>
    internal async Task RebuildFromJsonlAsync(
        IndexConnection idx,
        CancellationToken cancellationToken)
    {
        if (_sessionDir is null)
            throw new InvalidOperationException("Cannot rebuild before session directory is resolved.");

        string jsonlPath = Path.Combine(_sessionDir, "messages.jsonl");
        if (!File.Exists(jsonlPath))
            return;

        // Read all raw lines without compaction-collapse.
        string[] lines = await File.ReadAllLinesAsync(jsonlPath, cancellationToken)
            .ConfigureAwait(false);

        List<IndexEntry> entries = [];

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            IndexEntry? entry = TryParseLineToEntry(line);
            if (entry is null)
                continue;

            entries.Add(entry);
        }

        if (entries.Count == 0)
            return;

        // Re-embed in batches (document prefix).
        string[] prefixed = [.. entries.Select(e => NomicEmbedding.ApplyDocumentPrefix(e.Text))];
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await _embeddingGenerator.GenerateAsync(prefixed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Rebuild entries with fresh embeddings.
        List<IndexEntry> rebuildEntries = [];
        for (int i = 0; i < entries.Count; i++)
        {
            IndexEntry e = entries[i];
            rebuildEntries.Add(e with { Embedding = embeddings[i].Vector.ToArray() });
        }

        await idx.UpsertBatchAsync(rebuildEntries, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ExtractText(MessageRecord record)
    {
        // Concatenate text from all TextPart instances in the record.
        StringBuilder sb = new();
        foreach (Part part in record.Parts)
        {
            if (part is TextPart tp && !string.IsNullOrEmpty(tp.Text))
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(tp.Text);
            }
        }

        return sb.Length > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// Attempts to parse a raw JSONL line into an <see cref="IndexEntry"/> for rebuild.
    /// Parses the canonical <see cref="MessageRecord"/> shape (type: "message") and
    /// derives index text from <see cref="TextPart"/>s.
    /// Skips lines that are not <c>type:message</c>, have no text parts, or are malformed.
    /// </summary>
    private static IndexEntry? TryParseLineToEntry(string line)
    {
        try
        {
            SessionLogLine? logLine = JsonSerializer.Deserialize<SessionLogLine>(line);

            if (logLine is not MessageRecord record)
                return null;

            string text = ExtractText(record);
            if (string.IsNullOrWhiteSpace(text))
                return null;

            // Placeholder embedding — caller must overwrite before upserting.
            return new IndexEntry(
                EntryId:   record.EntryId,
                Role:      record.Role,
                Text:      text,
                Timestamp: record.Timestamp,
                Scope:     MemoryScope.Agent,
                Embedding: []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Converts a raw query string into an FTS5 MATCH expression.
    /// Splits on whitespace into individual terms, double-quote-escapes each, and
    /// joins with OR so that any term can contribute a match (improves multi-term
    /// recall with the trigram tokenizer — a single-phrase wrap would require the
    /// full contiguous substring to be present).
    /// Empty or all-whitespace query is returned as an empty string; callers should
    /// guard against passing empty strings to FTS5 MATCH.
    /// </summary>
    private static string EscapeFtsQuery(string query)
    {
        // Split on any whitespace; discard empty segments.
        string[] terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (terms.Length == 0)
            return string.Empty;

        // Each term is wrapped in double-quotes with internal " doubled — safe against
        // FTS5 syntax injection while still letting the trigram index match substrings.
        IEnumerable<string> escaped = terms.Select(
            t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");

        return string.Join(" OR ", escaped);
    }

    private void EnsureInitialized()
    {
        if (_index is null)
            throw new InvalidOperationException(
                $"{nameof(ShortTermMemory)} must be initialized before use. " +
                $"Call {nameof(InitializeAsync)} first.");
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        if (_index is not null)
        {
            _index.Dispose();
            _index = null;
        }
        return ValueTask.CompletedTask;
    }
}
