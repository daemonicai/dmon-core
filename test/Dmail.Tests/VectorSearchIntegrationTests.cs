using Dmail.Models;
using Dmail.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

#pragma warning disable SKEXP0070

namespace Dmail.Tests;

/// <summary>
/// 11.5: Integration tests for vec0 vector search via SK SqliteVec connector.
/// Uses known 384-dim float vectors — no ONNX model or web host required.
/// Asserts that SearchAsync returns records in correct cosine-similarity order.
/// </summary>
public sealed class VectorSearchIntegrationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteVectorStore _vectorStore = null!;
    private VectorStoreService _vectorStoreService = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dmail-vec-{Guid.NewGuid()}.db");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();

        _vectorStore = new SqliteVectorStore(cs);
        _vectorStoreService = new VectorStoreService(_vectorStore);

        // Ensure the vec0 collection table exists before upserting.
        await _vectorStoreService.Collection.EnsureCollectionExistsAsync();
    }

    public Task DisposeAsync()
    {
        try { SqliteConnection.ClearAllPools(); File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private static float[] MakeUnitVector(int dimensions, int hotIndex, float hotValue = 1.0f)
    {
        var v = new float[dimensions];
        v[hotIndex] = hotValue;
        return v;
    }

    private static float[] NormalizeVector(float[] v)
    {
        var mag = MathF.Sqrt(v.Sum(x => x * x));
        return mag == 0f ? v : v.Select(x => x / mag).ToArray();
    }

    [Fact]
    public async Task Upsert_ThenSearch_NearestVectorRanksFirst()
    {
        // Construct three 384-dim unit vectors:
        //   v1: hot on dim 0   → most similar to query (also hot on dim 0)
        //   v2: hot on dim 100 → dissimilar to query
        //   v3: hot on dim 200 → very dissimilar to query
        var v1 = MakeUnitVector(384, 0);
        var v2 = MakeUnitVector(384, 100);
        var v3 = MakeUnitVector(384, 200);

        await _vectorStoreService.UpsertVectorAsync("acct@test.com", 1, v1);
        await _vectorStoreService.UpsertVectorAsync("acct@test.com", 2, v2);
        await _vectorStoreService.UpsertVectorAsync("acct@test.com", 3, v3);

        // Query vector: hot on dim 0 (identical direction to v1)
        var query = new ReadOnlyMemory<float>(MakeUnitVector(384, 0));

        var results = new List<VectorSearchResult<EmailVector>>();
        await foreach (var r in _vectorStoreService.Collection.SearchAsync(query, 3))
            results.Add(r);

        Assert.Equal(3, results.Count);

        // v1 has cosine distance 0 to query (identical direction) — must rank first.
        // SqliteVec returns results ordered by ascending distance (nearest first).
        Assert.Equal("acct@test.com:1", results[0].Record.Id);

        // Score is cosine distance: 0 = identical, 1 = orthogonal.
        // Results come back nearest-first, so score must be non-decreasing.
        Assert.True(results[0].Score <= results[1].Score,
            $"Expected result[0].Score ({results[0].Score}) <= result[1].Score ({results[1].Score}) (ascending distance)");
        Assert.True(results[1].Score <= results[2].Score,
            $"Expected result[1].Score ({results[1].Score}) <= result[2].Score ({results[2].Score}) (ascending distance)");
    }

    [Fact]
    public async Task Upsert_BatchThenSearch_AllRecordsRetrievable()
    {
        var entries = Enumerable.Range(10, 5)
            .Select(i => ("batch@test.com", (uint)i, MakeUnitVector(384, i)))
            .ToList();

        await _vectorStoreService.UpsertBatchAsync(entries);

        // Query hot on dim 12 → uid=12 should rank first
        var query = new ReadOnlyMemory<float>(MakeUnitVector(384, 12));

        var results = new List<VectorSearchResult<EmailVector>>();
        await foreach (var r in _vectorStoreService.Collection.SearchAsync(query, 5))
            results.Add(r);

        Assert.Equal(5, results.Count);
        Assert.Equal("batch@test.com:12", results[0].Record.Id);
    }

    [Fact]
    public async Task UpsertTwice_SameId_Overwrites()
    {
        // First version: hot on dim 50
        await _vectorStoreService.UpsertVectorAsync("user@test.com", 99, MakeUnitVector(384, 50));

        // Second version: hot on dim 300
        await _vectorStoreService.UpsertVectorAsync("user@test.com", 99, MakeUnitVector(384, 300));

        // Query hot on dim 300 → updated vector should rank highest
        var query = new ReadOnlyMemory<float>(MakeUnitVector(384, 300));

        var results = new List<VectorSearchResult<EmailVector>>();
        await foreach (var r in _vectorStoreService.Collection.SearchAsync(query, 3))
            results.Add(r);

        Assert.NotEmpty(results);
        Assert.Equal("user@test.com:99", results[0].Record.Id);
    }

    [Fact]
    public async Task Search_NoRecords_ReturnsEmpty()
    {
        // Fresh collection — no records upserted in this test
        var emptyStore = new SqliteVectorStore(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetTempPath(), $"dmail-vec-empty-{Guid.NewGuid()}.db"),
                Pooling = false
            }.ToString());
        var emptyService = new VectorStoreService(emptyStore);
        await emptyService.Collection.EnsureCollectionExistsAsync();

        var query = new ReadOnlyMemory<float>(MakeUnitVector(384, 0));
        var results = new List<VectorSearchResult<EmailVector>>();
        await foreach (var r in emptyService.Collection.SearchAsync(query, 5))
            results.Add(r);

        Assert.Empty(results);
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task EmailVector_BuildId_FormatsCorrectly()
    {
        // Validate the key format used by VectorStoreService and HybridSearchService
        // when parsing "account:uid" from search results.
        const string account = "user@example.com";
        const uint uid = 42;
        var id = EmailVector.BuildId(account, uid);

        Assert.Equal("user@example.com:42", id);

        var parts = id.Split(':', 2);
        Assert.Equal(2, parts.Length);
        Assert.Equal(account, parts[0]);
        Assert.True(uint.TryParse(parts[1], out var parsedUid));
        Assert.Equal(uid, parsedUid);
    }
}
