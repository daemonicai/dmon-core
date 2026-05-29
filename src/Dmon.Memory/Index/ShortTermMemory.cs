using System.Text;
using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Memory.Embedding;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory.Index;

/// <summary>
/// Per-session <see cref="IShortTermMemory"/> backed by a three-table hybrid index
/// (<c>vec0</c> + FTS5 + content) in <c>&lt;sessionDir&gt;/index.db</c>.
///
/// Construction does NOT open the database — call <see cref="InitializeAsync"/> first.
/// The simplest correct design is chosen for <see cref="RecordAsync"/>: JSONL append then
/// inline SQLite upsert, so <see cref="SearchAsync"/> finds newly recorded turns immediately.
/// <see cref="FlushAsync"/> is a no-op durability barrier (JSONL is already durable on
/// each <see cref="RecordAsync"/> return; SQLite WAL checkpoints on close).
/// </summary>
public sealed class ShortTermMemory : IShortTermMemory, IAsyncDisposable
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IMessageAppender _messageAppender;
    private readonly ISessionStore _sessionStore;
    private readonly ISessionDirectoryResolver _directoryResolver;
    private readonly MemoryContext _context;
    private readonly ILogger<ShortTermMemory>? _logger;

    // Resolved on InitializeAsync; null until then.
    private IndexConnection? _index;
    private string? _sessionDir;

    // Guards writes for this instance (IMessageAppender is not concurrency-safe per session).
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ShortTermMemory(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IMessageAppender messageAppender,
        ISessionStore sessionStore,
        ISessionDirectoryResolver directoryResolver,
        MemoryContext context,
        ILogger<ShortTermMemory>? logger = null)
    {
        _embeddingGenerator = embeddingGenerator;
        _messageAppender = messageAppender;
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
    /// Ordering (synchronous inline design):
    /// 1. For each turn, serialize to <see cref="TurnLineRecord"/> and append to
    ///    <c>messages.jsonl</c> via <see cref="IMessageAppender"/> (durable source of truth).
    /// 2. Embed all turn texts in one batch (<c>search_document:</c> prefix).
    /// 3. Upsert content + vec0 + FTS5 in ONE SQLite transaction.
    ///
    /// A failure between steps 1 and 3 leaves JSONL lines written but not yet indexed.
    /// The index is recoverable by calling <c>InitializeAsync</c> again after a mismatch
    /// triggers rebuild, or by deleting <c>index.db</c>.
    /// </remarks>
    public async Task RecordAsync(
        IReadOnlyList<ChatMessage> turns,
        MemoryScope scope = MemoryScope.Agent,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (turns.Count == 0)
            return;

        // Collect the text + metadata for each turn before acquiring the write lock
        // (embedding is CPU-intensive; keep the lock window minimal).
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<(TurnLineRecord LineRecord, string Text)> prepared = [];

        foreach (ChatMessage turn in turns)
        {
            string text = ExtractText(turn);
            TurnLineRecord line = new(
                EntryId:   Guid.NewGuid().ToString(),
                Timestamp: now,
                Role:      turn.Role.Value,
                Text:      text,
                Scope:     scope);
            prepared.Add((line, text));
        }

        // Embed all texts in one batch (search_document: prefix for storage).
        string[] prefixed = [.. prepared.Select(p => NomicEmbedding.ApplyDocumentPrefix(p.Text))];
        GeneratedEmbeddings<Embedding<float>> embeddings =
            await _embeddingGenerator.GenerateAsync(prefixed, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Build index entries from embeddings.
        List<IndexEntry> entries = [];
        for (int i = 0; i < prepared.Count; i++)
        {
            (TurnLineRecord line, string text) = prepared[i];
            entries.Add(new IndexEntry(
                EntryId:   line.EntryId,
                Role:      line.Role,
                Text:      text,
                Timestamp: line.Timestamp,
                Scope:     scope,
                Embedding: embeddings[i].Vector.ToArray()));
        }

        // Serialise writes: JSONL append then SQLite upsert under one lock.
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Step 1: append each turn to messages.jsonl (durable source of truth).
            foreach ((TurnLineRecord line, _) in prepared)
            {
                await _messageAppender
                    .AppendAsync(_context.ConversationId, line, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Step 2: upsert into the hybrid index in a single transaction.
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

    private static string ExtractText(ChatMessage turn)
    {
        // Concatenate all text content parts.
        StringBuilder sb = new();
        foreach (AIContent part in turn.Contents)
        {
            if (part is TextContent tc)
            {
                if (sb.Length > 0)
                    sb.Append(' ');
                sb.Append(tc.Text);
            }
        }

        return sb.Length > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>
    /// Attempts to parse a raw JSONL line into an <see cref="IndexEntry"/> for rebuild.
    /// Recognizes the <see cref="TurnLineRecord"/> shape written by this implementation.
    /// Skips lines that do not match (e.g. compaction markers, future unknown types).
    /// Lines written before the <c>scope</c> field was added default to
    /// <see cref="MemoryScope.Agent"/> for forward-compatibility.
    /// </summary>
    private static IndexEntry? TryParseLineToEntry(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("entryId", out JsonElement entryIdEl) ||
                !root.TryGetProperty("role",    out JsonElement roleEl)    ||
                !root.TryGetProperty("text",    out JsonElement textEl)    ||
                !root.TryGetProperty("timestamp", out JsonElement tsEl))
            {
                return null;
            }

            string entryId = entryIdEl.GetString() ?? string.Empty;
            string role    = roleEl.GetString()    ?? string.Empty;
            string text    = textEl.GetString()    ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                return null;

            DateTimeOffset timestamp = tsEl.TryGetDateTimeOffset(out DateTimeOffset dto)
                ? dto
                : DateTimeOffset.UtcNow;

            // Legacy lines pre-dating the scope field default to Agent (most common case).
            MemoryScope scope = MemoryScope.Agent;
            if (root.TryGetProperty("scope", out JsonElement scopeEl) &&
                scopeEl.TryGetInt32(out int scopeInt))
            {
                scope = (MemoryScope)scopeInt;
            }

            // Placeholder embedding — caller must overwrite before upserting.
            return new IndexEntry(
                EntryId:   entryId,
                Role:      role,
                Text:      text,
                Timestamp: timestamp,
                Scope:     scope,
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
