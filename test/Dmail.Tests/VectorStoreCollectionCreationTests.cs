using Dmail.Models;
using Dmail.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

#pragma warning disable SKEXP0070

namespace Dmail.Tests;

/// <summary>
/// Regression test for the missing EnsureCollectionExistsAsync() call at startup.
/// Verifies that VectorStoreService.EnsureCollectionAsync() creates the vec0 table
/// so that UpsertVectorAsync succeeds — the production path that was broken.
/// </summary>
public sealed class VectorStoreCollectionCreationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteVectorStore _vectorStore = null!;
    private VectorStoreService _service = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dmail-ensure-{Guid.NewGuid()}.db");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();

        _vectorStore = new SqliteVectorStore(cs);
        _service = new VectorStoreService(_vectorStore);

        // Do NOT call EnsureCollectionExistsAsync() here — the tests do it themselves
        // to exercise the production startup path (VectorStoreService.EnsureCollectionAsync).
    }

    public Task DisposeAsync()
    {
        try { SqliteConnection.ClearAllPools(); File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private static float[] MakeUnitVector(int dimensions, int hotIndex)
    {
        var v = new float[dimensions];
        v[hotIndex] = 1.0f;
        return v;
    }

    [Fact]
    public async Task EnsureCollectionAsync_CreatesTable_UpsertSucceeds()
    {
        // This is the production startup sequence: EnsureCollectionAsync() then upsert.
        // Before the fix, nothing called EnsureCollectionExistsAsync() at startup, so
        // every upsert threw SqliteException: 'no such table: emails'.
        await _service.EnsureCollectionAsync();

        var ex = await Record.ExceptionAsync(() =>
            _service.UpsertVectorAsync("startup@test.com", 1u, MakeUnitVector(384, 0)));

        Assert.Null(ex);
    }

    [Fact]
    public async Task EnsureCollectionAsync_ThenUpsert_RecordRetrievableViaSearch()
    {
        await _service.EnsureCollectionAsync();

        var vector = MakeUnitVector(384, 42);
        await _service.UpsertVectorAsync("verify@test.com", 7u, vector);

        var query = new ReadOnlyMemory<float>(MakeUnitVector(384, 42));
        var results = new List<VectorSearchResult<EmailVector>>();
        await foreach (var r in _service.Collection.SearchAsync(query, 1))
            results.Add(r);

        Assert.Single(results);
        Assert.Equal("verify@test.com:7", results[0].Record.Id);
    }

    [Fact]
    public async Task EnsureCollectionAsync_CalledTwice_IsIdempotent()
    {
        // EnsureCollectionExistsAsync must be safe to call multiple times (uses IF NOT EXISTS).
        await _service.EnsureCollectionAsync();
        var ex = await Record.ExceptionAsync(() => _service.EnsureCollectionAsync());
        Assert.Null(ex);
    }

    [Fact]
    public async Task UpsertVectorAsync_WithoutEnsureCollection_ThrowsSqliteException()
    {
        // Documents the root cause: without EnsureCollectionAsync(), the table does not
        // exist and the first upsert throws. This test pins the pre-fix behaviour so a
        // future regression (e.g. silently ignoring the missing startup call) is caught.
        var freshDbPath = Path.Combine(Path.GetTempPath(), $"dmail-noensure-{Guid.NewGuid()}.db");
        var cs = new SqliteConnectionStringBuilder { DataSource = freshDbPath, Pooling = false }.ToString();
        var freshStore = new SqliteVectorStore(cs);
        var freshService = new VectorStoreService(freshStore);

        try
        {
            var ex = await Record.ExceptionAsync(() =>
                freshService.UpsertVectorAsync("no-table@test.com", 99u, MakeUnitVector(384, 0)));

            // The connector must throw when the table is absent.
            Assert.NotNull(ex);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(freshDbPath); } catch { }
        }
    }
}
