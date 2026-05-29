using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Memory.Embedding;
using Dmon.Memory.Index;
using Dmon.Memory.Tests.Stubs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

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

        // Record two good turns.
        await memory.RecordAsync([
            new ChatMessage(ChatRole.User, goodText1),
            new ChatMessage(ChatRole.Assistant, goodText2),
        ]);

        int rowsBefore = CountContentRows(_fixture.IndexDbPath(sessionId));
        Assert.Equal(2, rowsBefore);

        // Attempt to record the faulting turn — should throw.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memory.RecordAsync([new ChatMessage(ChatRole.User, faultText)]));

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
    }

    // ── 4.4.2 ReadMessagesAsync matches verbatim messages.jsonl ─────────────

    [Fact]
    public async Task ReadMessagesAsync_MatchesVerbatimJsonlContent()
    {
        const string text1 = "ribosomes translate messenger RNA into proteins";
        const string text2 = "ATP synthase produces adenosine triphosphate";

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory _ = memory;

        await memory.RecordAsync([
            new ChatMessage(ChatRole.User, text1),
            new ChatMessage(ChatRole.Assistant, text2),
        ]);

        // ReadMessagesAsync returns deserialized objects (via SessionStore).
        IReadOnlyList<object> messages = await memory.ReadMessagesAsync(applyCompaction: false);

        // Read the raw JSONL for comparison.
        string[] rawLines = await File.ReadAllLinesAsync(_fixture.MessagesJsonlPath(sessionId));
        string[] nonEmpty = rawLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

        // The count must match.
        Assert.Equal(nonEmpty.Length, messages.Count);

        // Each message, when re-serialized, must round-trip to contain the original text.
        string combined = JsonSerializer.Serialize(messages);
        Assert.Contains(text1, combined, StringComparison.Ordinal);
        Assert.Contains(text2, combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadMessagesAsync_TextsPreservedVerbatim()
    {
        const string text = "CRISPR-Cas9 enables precise genome editing in living cells";

        (ShortTermMemory memory, string sessionId) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory _ = memory;

        await memory.RecordAsync([new ChatMessage(ChatRole.User, text)]);

        // The raw JSONL must contain the exact text.
        string[] rawLines = await File.ReadAllLinesAsync(_fixture.MessagesJsonlPath(sessionId));
        string rawContent = string.Join('\n', rawLines);
        Assert.Contains(text, rawContent, StringComparison.Ordinal);

        // ReadMessagesAsync must also surface it.
        IReadOnlyList<object> messages = await memory.ReadMessagesAsync(applyCompaction: false);
        string messagesJson = JsonSerializer.Serialize(messages);
        Assert.Contains(text, messagesJson, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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
