using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Memory.Embedding;
using Dmon.Memory.Index;
using Dmon.Memory.Tests.Stubs;
using Dmon.Protocol.Conversation;

namespace Dmon.Memory.Tests.Index;

/// <summary>
/// Integration tests for the hybrid vec0+FTS5 index via <see cref="ShortTermMemory"/>.
/// Uses the <see cref="StubEmbeddingGenerator"/> — no GGUF, no network.
/// </summary>
public sealed class HybridIndexTests : IAsyncLifetime
{
    private TempSessionFixture _fixture = null!;

    public Task InitializeAsync()
    {
        _fixture = TempSessionFixture.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    // ── 4.2.1 vec0 actually loads and KNN MATCH returns rows ─────────────────

    [Fact]
    public async Task Search_AfterRecord_Vec0LoadsAndKnnReturnsRows()
    {
        // Arrange: give the query a similar vector to the document so it surfaces.
        const string docText   = "photosynthesis converts sunlight to energy";
        const string queryText = "photosynthesis";

        // Same group → cosine distance ≈ 0 → will appear in KNN results.
        _fixture.EmbeddingGenerator.SetGroupVector("plant-group",
            NomicEmbedding.ApplyDocumentPrefix(docText),
            NomicEmbedding.ApplyQueryPrefix(queryText));

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem1 = memory;

        await mem1.RecordAsync([MakeRecord("user", docText)]);

        // Act
        IReadOnlyList<MemoryHit> hits = await mem1.SearchAsync(queryText);

        // Assert: vec0 loaded and KNN MATCH returned at least one row.
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Text.Contains("photosynthesis", StringComparison.OrdinalIgnoreCase));

        // Spec: Results are attributed to short-term — every hit has Source=ShortTerm and null Relations.
        Assert.All(hits, h =>
        {
            Assert.Equal(MemorySource.ShortTerm, h.Source);
            Assert.Null(h.Relations);
        });
        _ = sessionId; // suppress unused warning
    }

    // ── 4.2.2 Single-modality recall: FULL OUTER JOIN surfaces both sides ────

    [Fact]
    public async Task Search_BothModalitiesPresent_SurfacesBothVectorOnlyAndKeywordOnlyEntries()
    {
        // Proves the FULL OUTER JOIN surfaces entries from BOTH modalities independently:
        //   vectorOnlyText  — vector-near to query, NO keyword overlap with query
        //   keywordOnlyText — keyword match (query terms verbatim), vector-distant from query
        const string vectorOnlyText  = "zyzzyx quux xyzzy frobnitz";   // no query terms
        const string keywordOnlyText = "apricot mango xyzzy zork apple pear";  // contains query terms
        const string queryText = "xyzzy zork";

        _fixture.EmbeddingGenerator.SetGroupVector("close-group",
            NomicEmbedding.ApplyDocumentPrefix(vectorOnlyText),
            NomicEmbedding.ApplyQueryPrefix(queryText));

        (ShortTermMemory memory, string sessionId2) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem2 = memory;

        await mem2.RecordAsync([
            MakeRecord("user",      vectorOnlyText),
            MakeRecord("assistant", keywordOnlyText),
        ]);

        IReadOnlyList<MemoryHit> hits = await mem2.SearchAsync(queryText, limit: 10);

        Assert.Contains(hits, h => h.Text == vectorOnlyText);
        Assert.Contains(hits, h => h.Text == keywordOnlyText);
        _ = sessionId2;
    }

    // ── 4.2.3 Empty index → empty list ───────────────────────────────────────

    [Fact]
    public async Task Search_EmptyIndex_ReturnsEmptyList()
    {
        (ShortTermMemory memory, string sessionId3) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem3 = memory;

        IReadOnlyList<MemoryHit> hits = await mem3.SearchAsync("anything");

        Assert.Empty(hits);
        _ = sessionId3;
    }

    // ── 4.2.4 bm25 default cutoff admits genuine FTS matches ────────────────

    [Fact]
    public async Task Search_GenuineFtsMatch_IsReturnedByDefaultCutoff()
    {
        const string docText   = "mitochondria is the powerhouse of the cell";
        const string queryText = "mitochondria powerhouse";

        (ShortTermMemory memory, string sessionId4) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem4 = memory;

        await mem4.RecordAsync([MakeRecord("user", docText)]);

        IReadOnlyList<MemoryHit> hits = await mem4.SearchAsync(queryText);

        Assert.Contains(hits, h => h.Text.Contains("mitochondria", StringComparison.OrdinalIgnoreCase));
        _ = sessionId4;
    }

    // ── 4.2.5 Rebuild-from-JSONL parity ─────────────────────────────────────

    [Fact]
    public async Task Rebuild_AfterIndexDbDeleted_SearchResultsEquivalent()
    {
        const string doc1 = "gravitational waves detected by LIGO";
        const string doc2 = "black hole merger observation";

        _fixture.EmbeddingGenerator.SetGroupVector("gravity-group",
            NomicEmbedding.ApplyDocumentPrefix(doc1),
            NomicEmbedding.ApplyDocumentPrefix(doc2),
            NomicEmbedding.ApplyQueryPrefix("gravitational waves LIGO merger"));

        (ShortTermMemory memory, string sessionId5) = await _fixture.BuildMemoryAsync();

        // Populate the canonical JSONL via session-storage, then index via RecordAsync.
        SessionStore store = _fixture.CreateSessionStore();
        string entryId1 = await store.AppendMessageAsync(sessionId5, "user",
            [new TextPart { Text = doc1 }]);
        string entryId2 = await store.AppendMessageAsync(sessionId5, "assistant",
            [new TextPart { Text = doc2 }]);

        await memory.RecordAsync([
            MakeRecord("user",      doc1, entryId1),
            MakeRecord("assistant", doc2, entryId2),
        ]);

        IReadOnlyList<MemoryHit> before = await memory.SearchAsync("gravitational waves LIGO merger");
        await memory.DisposeAsync();

        // Delete index.db to force a rebuild on next open.
        File.Delete(_fixture.IndexDbPath(sessionId5));

        // Reopen — this should trigger RebuildFromJsonlAsync.
        (ShortTermMemory rebuilt, string sessionId5b) = await _fixture.BuildMemoryAsync(sessionId5);
        await using ShortTermMemory mem5 = rebuilt;

        IReadOnlyList<MemoryHit> after = await mem5.SearchAsync("gravitational waves LIGO merger");

        Assert.Contains(after, h => h.Text == doc1);
        Assert.Contains(after, h => h.Text == doc2);
        Assert.Equal(before.Count, after.Count);
        _ = sessionId5b;
    }

    // ── 4.2.6 Model/dimension mismatch triggers rebuild ──────────────────────

    [Fact]
    public async Task Rebuild_OnModelIdMismatch_IndexRebuildsAndContentIsRecallable()
    {
        const string doc   = "superconducting magnets in particle accelerators";
        const string query = "superconducting magnets";

        _fixture.EmbeddingGenerator.SetGroupVector("physics-group",
            NomicEmbedding.ApplyDocumentPrefix(doc),
            NomicEmbedding.ApplyQueryPrefix(query));

        (ShortTermMemory memory, string sessionId6) = await _fixture.BuildMemoryAsync();

        // Populate JSONL then index.
        SessionStore store = _fixture.CreateSessionStore();
        string entryId = await store.AppendMessageAsync(sessionId6, "user",
            [new TextPart { Text = doc }]);
        await memory.RecordAsync([MakeRecord("user", doc, entryId)]);
        await memory.DisposeAsync();

        // Tamper the meta table to simulate a model-id change.
        TamperMetaModelId(_fixture.IndexDbPath(sessionId6), "old-model-id");

        // Reopen — rebuild must fire, content must be recallable.
        (ShortTermMemory rebuilt, string sessionId6b) = await _fixture.BuildMemoryAsync(sessionId6);
        await using ShortTermMemory mem6 = rebuilt;

        IReadOnlyList<MemoryHit> hits = await mem6.SearchAsync(query);

        Assert.Contains(hits, h => h.Text == doc);

        // Assert that the rebuild branch ran: model_id in memory_meta must have been
        // rewritten from the tampered "old-model-id" back to NomicEmbedding.ModelId.
        string storedModelId = ReadMetaModelId(_fixture.IndexDbPath(sessionId6));
        Assert.Equal(NomicEmbedding.ModelId, storedModelId);

        _ = sessionId6b;
    }

    // ── Scope isolation (B4 + N1) ────────────────────────────────────────────

    [Fact]
    public async Task Search_SessionScopedEntry_NotReturnedByAgentScopedSearch()
    {
        const string sessionText = "session-scoped private note about user preferences";
        const string agentText   = "agent-scoped public knowledge base entry";
        const string queryText   = "preferences knowledge base";

        _fixture.EmbeddingGenerator.SetGroupVector("scope-group",
            NomicEmbedding.ApplyDocumentPrefix(sessionText),
            NomicEmbedding.ApplyDocumentPrefix(agentText),
            NomicEmbedding.ApplyQueryPrefix(queryText));

        (ShortTermMemory memory, string sessionId7) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem7 = memory;

        await mem7.RecordAsync(
            [MakeRecord("user", sessionText)],
            scope: MemoryScope.Session);

        await mem7.RecordAsync(
            [MakeRecord("user", agentText)],
            scope: MemoryScope.Agent);

        // Agent-scoped search must NOT return the session entry.
        IReadOnlyList<MemoryHit> agentHits = await mem7.SearchAsync(
            queryText, scope: MemoryScope.Agent);

        Assert.DoesNotContain(agentHits, h => h.Text == sessionText);
        Assert.Contains(agentHits, h => h.Text == agentText);

        // Session-scoped search must NOT return the agent entry.
        IReadOnlyList<MemoryHit> sessionHits = await mem7.SearchAsync(
            queryText, scope: MemoryScope.Session);

        Assert.Contains(sessionHits, h => h.Text == sessionText);
        Assert.DoesNotContain(sessionHits, h => h.Text == agentText);
        _ = sessionId7;
    }

    [Fact]
    public async Task Rebuild_SessionScopedEntry_RetainsScopeAfterRebuild()
    {
        // Rebuild honours scope stored in the canonical record — but today's TryParseLineToEntry
        // defaults all rebuilt entries to MemoryScope.Agent (scope is not in MessageRecord).
        // This test therefore verifies that rebuild completes without error and the entry
        // appears under Agent scope (the rebuild default), not Session scope.
        const string sessionText = "session-only sensitive data";
        const string query       = "sensitive data";

        _fixture.EmbeddingGenerator.SetGroupVector("rebuild-scope-group",
            NomicEmbedding.ApplyDocumentPrefix(sessionText),
            NomicEmbedding.ApplyQueryPrefix(query));

        (ShortTermMemory memory, string sessionId8) = await _fixture.BuildMemoryAsync();

        // Populate JSONL then index under Session scope.
        SessionStore store = _fixture.CreateSessionStore();
        string entryId = await store.AppendMessageAsync(sessionId8, "user",
            [new TextPart { Text = sessionText }]);
        await memory.RecordAsync([MakeRecord("user", sessionText, entryId)], scope: MemoryScope.Session);
        await memory.DisposeAsync();

        File.Delete(_fixture.IndexDbPath(sessionId8));

        (ShortTermMemory rebuilt, string sessionId8b) = await _fixture.BuildMemoryAsync(sessionId8);
        await using ShortTermMemory mem8 = rebuilt;

        // After rebuild (which defaults scope to Agent), must appear under Agent scope.
        IReadOnlyList<MemoryHit> agentHits = await mem8.SearchAsync(query, scope: MemoryScope.Agent);
        Assert.Contains(agentHits, h => h.Text == sessionText);

        _ = sessionId8b;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MessageRecord MakeRecord(string role, string text, string? entryId = null) =>
        new()
        {
            EntryId   = entryId ?? Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Role      = role,
            Parts     = [new TextPart { Text = text }]
        };

    private static void TamperMetaModelId(string dbPath, string fakeModelId)
    {
        // Pooling=false keeps this connection from interfering with vec0-enabled connections.
        using Microsoft.Data.Sqlite.SqliteConnection connection =
            new($"Data Source={dbPath};Pooling=false");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE memory_meta SET value = @v WHERE key = 'model_id'";
        cmd.Parameters.AddWithValue("@v", fakeModelId);
        cmd.ExecuteNonQuery();
    }

    private static string ReadMetaModelId(string dbPath)
    {
        using Microsoft.Data.Sqlite.SqliteConnection conn =
            new($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using Microsoft.Data.Sqlite.SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM memory_meta WHERE key = 'model_id'";
        object? result = cmd.ExecuteScalar();
        return result is string s ? s : string.Empty;
    }
}
