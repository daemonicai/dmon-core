using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Core.Session;
using Dmon.Memory.Embedding;
using Dmon.Memory.Index;
using Dmon.Memory.Tests.Stubs;
using Dmon.Protocol.Conversation;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dmon.Memory.Tests.Index;

/// <summary>Task 4.4 — Record path: transactional consistency and verbatim ReadMessagesAsync.</summary>
public sealed class RecordPathTests : IAsyncLifetime
{
    private TempSessionFixture _fixture = null!;

    public Task InitializeAsync()
    {
        _fixture = TempSessionFixture.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    // ── 4.4.1 Transactional consistency: fault during embedding leaves index intact ──

    [Fact]
    public async Task RecordAsync_EmbeddingFaultMidBatch_IndexHasNothingForFailedTurn()
    {
        // Record two good turns first to establish a baseline row count.
        const string goodText1   = "enzyme kinetics Michaelis Menten";
        const string goodText2   = "protein folding chaperones";
        const string faultText   = "this text will cause the embedder to throw";

        // The faulting input is the prefixed form that the embedder sees.
        string faultPrefixed = NomicEmbedding.ApplyDocumentPrefix(faultText);
        _fixture.EmbeddingGenerator.ShouldThrowOn(faultPrefixed);

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();

        // Record two good turns (index-only — no canonical write).
        await memory.RecordAsync([
            MakeRecord("user", goodText1),
            MakeRecord("assistant", goodText2),
        ]);

        int rowsBefore = CountContentRows(_fixture.IndexDbPath(sessionId));
        Assert.Equal(2, rowsBefore);

        // Attempt to record the faulting turn — should throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memory.RecordAsync([MakeRecord("user", faultText)]));

        // memory_content must have exactly 2 rows — the faulted turn's rows must not be committed.
        // content, vec, and fts are all written in one transaction, so content row count is
        // the authoritative indicator: if content = 2 the whole transaction was rolled back.
        int rowsAfter = CountContentRows(_fixture.IndexDbPath(sessionId));
        Assert.Equal(2, rowsAfter);

        // Confirm no partial vec/FTS rows were left for the failed turn.
        // CountVecRows queries memory_vec directly (vec0-loaded connection).
        // CountFtsRows queries memory_fts directly (FTS5 built-in, no vec0 needed).
        Assert.Equal(2, CountVecRows(_fixture.IndexDbPath(sessionId)));
        Assert.Equal(2, CountFtsRows(_fixture.IndexDbPath(sessionId)));

        await memory.DisposeAsync();
        _ = sessionId;
    }

    // ── 4.4.2 RecordAsync is index-only — does not write to messages.jsonl ──

    [Fact]
    public async Task RecordAsync_DoesNotWriteToMessagesJsonl()
    {
        const string text1 = "ribosomes translate messenger RNA into proteins";
        const string text2 = "ATP synthase produces adenosine triphosphate";

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory _ = memory;

        string[] linesBefore = await File.ReadAllLinesAsync(_fixture.MessagesJsonlPath(sessionId));
        int countBefore = linesBefore.Count(l => !string.IsNullOrWhiteSpace(l));

        await memory.RecordAsync([
            MakeRecord("user",      text1),
            MakeRecord("assistant", text2),
        ]);

        string[] linesAfter = await File.ReadAllLinesAsync(_fixture.MessagesJsonlPath(sessionId));
        int countAfter = linesAfter.Count(l => !string.IsNullOrWhiteSpace(l));

        // Index-only: messages.jsonl must be unchanged.
        Assert.Equal(countBefore, countAfter);
    }

    // ── 4.4.3 RecordAsync keys index on supplied entryId ────────────────────

    [Fact]
    public async Task RecordAsync_KeysIndexOnSuppliedEntryId()
    {
        const string text = "CRISPR-Cas9 enables precise genome editing in living cells";
        string expectedEntryId = Guid.NewGuid().ToString();

        _fixture.EmbeddingGenerator.SetGroupVector("crispr-group",
            NomicEmbedding.ApplyDocumentPrefix(text),
            NomicEmbedding.ApplyQueryPrefix(text));

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem = memory;

        MessageRecord record = MakeRecord("user", text, expectedEntryId);
        await mem.RecordAsync([record]);

        IReadOnlyList<MemoryHit> hits = await mem.SearchAsync(text);

        Assert.Single(hits);
        Assert.Equal(expectedEntryId, hits[0].Id);
        _ = sessionId;
    }

    // ── 4.4.4 ReadMessagesAsync still works (delegates to SessionStore JSONL) ─

    [Fact]
    public async Task ReadMessagesAsync_ReturnsCanonicalJsonlContent()
    {
        const string text1 = "ribosomes translate messenger RNA into proteins";
        const string text2 = "ATP synthase produces adenosine triphosphate";

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory mem = memory;

        // Write directly to the session log (simulating what session-storage does).
        IConfiguration config = new ConfigurationBuilder().Build();
        FixedRootResolver resolver = new(_fixture.Root);
        IAttachmentStore attachmentStore = new AttachmentStore(resolver, config);
        SessionStore store = new(resolver, attachmentStore,
            NullLogger<SessionStore>.Instance,
            NullLoggerFactory.Instance, config);

        await store.AppendMessageAsync(sessionId, "user", [new TextPart { Text = text1 }]);
        await store.AppendMessageAsync(sessionId, "assistant", [new TextPart { Text = text2 }]);

        IReadOnlyList<object> messages = await mem.ReadMessagesAsync(applyCompaction: false);
        string messagesJson = JsonSerializer.Serialize(messages);

        Assert.Equal(2, messages.Count);
        Assert.Contains(text1, messagesJson, StringComparison.Ordinal);
        Assert.Contains(text2, messagesJson, StringComparison.Ordinal);
        _ = sessionId;
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

    private sealed class FixedRootResolver(string root) : ISessionDirectoryResolver
    {
        public string Resolve(string workingDirectory) => root;
    }

    private static int CountContentRows(string dbPath)
        => CountRows(dbPath, "SELECT COUNT(*) FROM memory_content");

    private static int CountVecRows(string dbPath)
    {
        // memory_vec is a vec0 virtual table — querying it requires vec0 to be loaded.
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=false");
        conn.Open();
        SqliteVecLoader.LoadVec0(conn);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM memory_vec";
        object? result = cmd.ExecuteScalar();
        return result is long l ? (int)l : Convert.ToInt32(result);
    }

    private static int CountFtsRows(string dbPath)
    {
        // memory_fts is an FTS5 virtual table — FTS5 is built into SQLite; no vec0 needed.
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM memory_fts";
        object? result = cmd.ExecuteScalar();
        return result is long l ? (int)l : Convert.ToInt32(result);
    }

    private static int CountRows(string dbPath, string sql)
    {
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=false");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        object? result = cmd.ExecuteScalar();
        return result is long l ? (int)l : Convert.ToInt32(result);
    }
}
