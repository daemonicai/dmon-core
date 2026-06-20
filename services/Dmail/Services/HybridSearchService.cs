using Daemonic.Dmail.Data;
using Daemonic.Dmail.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;

namespace Daemonic.Dmail.Services;

public sealed class HybridSearchService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly VectorStoreService _vectorStore;
    private readonly EmbeddingService _embedding;
    private const int DefaultTopK = 10;
    private const int MaxResults = 100;
    private const int SnippetMaxChars = 150;
    private const int RrfK = 60;

    public HybridSearchService(
        ISqliteConnectionFactory connectionFactory,
        VectorStoreService vectorStore,
        EmbeddingService embedding)
    {
        _connectionFactory = connectionFactory;
        _vectorStore = vectorStore;
        _embedding = embedding;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct = default)
    {
        var maxResults = Math.Min(request.MaxResults > 0 ? request.MaxResults : DefaultTopK, MaxResults);

        // Build structured filter clause (task 7.4)
        var (filterClause, filterParams) = BuildFilterClause(request);

        // Run FTS5 keyword search (task 7.1)
        var ftsResults = request.Keywords is { Length: > 0 }
            ? await Fts5SearchAsync(request.Keywords, filterClause, filterParams, ct)
            : new Dictionary<long, (uint Uid, string Account, string Subject, string From, string Date, string? Snippet, float Score)>();

        // Run vector semantic search (task 7.2)
        var vecResults = request.Semantic is { Length: > 0 }
            ? await VectorSearchAsync(request.Semantic, filterClause, filterParams, ct)
            : new Dictionary<string, (uint Uid, string Account, float Score)>();

        // Task 7.3: RRF fusion
        var fused = ReciprocalRankFusion(ftsResults, vecResults, maxResults);

        // Task 7.5: Build enriched results with per-path scores
        var results = await EnrichResultsAsync(fused, ftsResults, vecResults, ct);

        return new SearchResponse
        {
            Results = results.ToArray(),
            TotalFound = ftsResults.Count + vecResults.Count - fused.Count
        };
    }

    // ---- Task 7.1: FTS5 BM25 keyword search ----

    private async Task<Dictionary<long, (uint Uid, string Account, string Subject, string From, string Date, string? Snippet, float Score)>> Fts5SearchAsync(
        string[] keywords, string filterClause, List<SqliteParameter> filterParams, CancellationToken ct)
    {
        var results = new Dictionary<long, (uint, string, string, string, string, string?, float)>();
        var ftsQuery = string.Join(" AND ", keywords.Select(k => $"\"{k.Replace("\"", "\"\"")}\""));

        var sql = $@"
            SELECT e.rowid, e.uid, e.account, e.subject, e.from_addr, e.date,
                   snippet(emails_fts, 1, '<mark>', '</mark>', '...', 32) AS snippet,
                   bm25(emails_fts, 1.0, 0.75) AS score
            FROM emails_fts
            JOIN data_emails e ON emails_fts.rowid = e.rowid
            WHERE emails_fts MATCH @query
            {filterClause}
            ORDER BY score
            LIMIT {MaxResults}";

        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        foreach (var p in filterParams) cmd.Parameters.Add(p);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var rowid = reader.GetInt64(0);
            results[rowid] = (
                (uint)reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetFloat(7)
            );
        }

        return results;
    }

    // ---- Task 7.2: Vector similarity search ----

    private async Task<Dictionary<string, (uint Uid, string Account, float Score)>> VectorSearchAsync(
        string semantic, string filterClause, List<SqliteParameter> filterParams, CancellationToken ct)
    {
        var results = new Dictionary<string, (uint, string, float)>();

        if (!_embedding.IsModelReady) return results;

        // Embed the query text
        var queryVector = await _embedding.GenerateEmbeddingAsync(semantic, ct);

        // Search via VectorStoreCollection
        var searchResults = _vectorStore.Collection.SearchAsync(
            new ReadOnlyMemory<float>(queryVector),
            MaxResults,
            new VectorSearchOptions<EmailVector>(),
            ct);

        await foreach (var result in searchResults.WithCancellation(ct))
        {
            var record = result.Record;
            var id = record.Id; // "account:uid"
            var parts = id.Split(':', 2);
            if (parts.Length == 2 && uint.TryParse(parts[1], out var uid))
            {
                // SqliteVec returns cosine distance (0 = identical, 1 = orthogonal).
                // Convert to similarity so higher score = better match, consistent with
                // the OrderByDescending ranking in ReciprocalRankFusion.
                var similarity = 1.0f - (float)(result.Score ?? 1.0);
                results[id] = (uid, parts[0], similarity);
            }
        }

        return results;
    }

    // ---- Task 7.3: Reciprocal Rank Fusion (RRF) ----

    private static List<(long? RowId, string? VecId)> ReciprocalRankFusion(
        Dictionary<long, (uint Uid, string Account, string Subject, string From, string Date, string? Snippet, float Score)> ftsResults,
        Dictionary<string, (uint Uid, string Account, float Score)> vecResults,
        int topK)
    {
        // Build key → (rowId, vecId) lookup
        var keyToIds = new Dictionary<string, (long? RowId, string? VecId)>();
        var ftsKeys = new List<string>();

        foreach (var kv in ftsResults.OrderByDescending(kv => kv.Value.Score))
        {
            var key = $"{kv.Value.Account}:{kv.Value.Uid}";
            ftsKeys.Add(key);
            keyToIds[key] = (kv.Key, null);
        }

        var vecKeys = new List<string>();
        foreach (var kv in vecResults.OrderByDescending(kv => kv.Value.Score))
        {
            var key = $"{kv.Value.Account}:{kv.Value.Uid}";
            vecKeys.Add(key);
            if (keyToIds.TryGetValue(key, out var existing))
            {
                keyToIds[key] = (existing.RowId, kv.Key);
            }
            else
            {
                keyToIds[key] = (null, kv.Key);
            }
        }

        // Use extracted RRF utility for fusion
        var fusedKeys = Services.ReciprocalRankFusion.Fuse(ftsKeys, vecKeys, RrfK);

        return fusedKeys
            .Take(topK)
            .Select(key => keyToIds.TryGetValue(key, out var ids) ? ids : default)
            .Where(ids => ids.RowId != null || ids.VecId != null)
            .ToList();
    }

    // ---- Task 7.5: Enrich results with per-path scores ----

    private async Task<List<SearchResult>> EnrichResultsAsync(
        List<(long? RowId, string? VecId)> fused,
        Dictionary<long, (uint Uid, string Account, string Subject, string From, string Date, string? Snippet, float Score)> ftsResults,
        Dictionary<string, (uint Uid, string Account, float Score)> vecResults,
        CancellationToken ct)
    {
        var results = new List<SearchResult>();

        foreach (var (rowId, vecId) in fused)
        {
            SearchResult result;

            if (rowId.HasValue && ftsResults.TryGetValue(rowId.Value, out var fts))
            {
                var ftsScore = fts.Score;
                float? vecScore = null;
                string matchType = "fts";

                if (vecId != null && vecResults.TryGetValue(vecId, out var vec))
                {
                    vecScore = vec.Score;
                    matchType = "hybrid";
                }

                result = new SearchResult
                {
                    Uid = fts.Uid,
                    Subject = fts.Subject,
                    From = fts.From,
                    Date = fts.Date,
                    Snippet = fts.Snippet ?? TruncateBody(fts.Subject, SnippetMaxChars),
                    MatchType = matchType,
                    FtsScore = ftsScore,
                    VectorScore = vecScore,
                    HybridScore = (float?)(ftsScore + (vecScore ?? 0))
                };
            }
            else if (vecId != null && vecResults.TryGetValue(vecId, out var vec))
            {
                // Vector-only match — fetch details from data_emails
                var details = await GetEmailDetailsAsync(vec.Account, vec.Uid, ct);
                result = new SearchResult
                {
                    Uid = vec.Uid,
                    Subject = details.Subject,
                    From = details.From,
                    Date = details.Date,
                    Snippet = details.Snippet,
                    MatchType = "vector",
                    FtsScore = null,
                    VectorScore = vec.Score,
                    HybridScore = vec.Score
                };
            }
            else
            {
                continue;
            }

            results.Add(result);
        }

        return results;
    }

    private async Task<(string Subject, string From, string Date, string? Snippet)> GetEmailDetailsAsync(
        string account, uint uid, CancellationToken ct)
    {
        await using var connection = await _connectionFactory.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT subject, from_addr, date, body FROM data_emails WHERE account = @account AND uid = @uid";
        cmd.Parameters.AddWithValue("@account", account);
        cmd.Parameters.AddWithValue("@uid", (long)uid);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var body = reader.IsDBNull(3) ? "" : reader.GetString(3);
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                TruncateBody(body, SnippetMaxChars)
            );
        }

        return ("Unknown", "Unknown", "", null);
    }

    // ---- Task 7.4: Structured filter builder ----

    private static (string Clause, List<SqliteParameter> Params) BuildFilterClause(SearchRequest request)
    {
        var clauses = new List<string>();
        var parameters = new List<SqliteParameter>();
        int paramIdx = 0;

        if (!string.IsNullOrEmpty(request.Account))
        {
            clauses.Add($"e.account = @filter{paramIdx}");
            parameters.Add(new SqliteParameter($"@filter{paramIdx}", request.Account));
            paramIdx++;
        }

        if (!string.IsNullOrEmpty(request.From))
        {
            clauses.Add($"e.from_addr = @filter{paramIdx}");
            parameters.Add(new SqliteParameter($"@filter{paramIdx}", request.From));
            paramIdx++;
        }

        if (!string.IsNullOrEmpty(request.Since) && DateTime.TryParse(request.Since, out var since))
        {
            clauses.Add($"e.date >= @filter{paramIdx}");
            parameters.Add(new SqliteParameter($"@filter{paramIdx}", since.ToString("O")));
            paramIdx++;
        }

        var clause = clauses.Count > 0 ? "AND " + string.Join(" AND ", clauses) : "";
        return (clause, parameters);
    }

    private static string TruncateBody(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length > maxChars ? text[..maxChars] + "..." : text;
    }
}
