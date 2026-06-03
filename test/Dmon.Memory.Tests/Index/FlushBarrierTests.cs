using Dmon.Abstractions.Memory;
using Dmon.Memory.Embedding;
using Dmon.Memory.Index;
using Dmon.Memory.Tests.Stubs;
using Dmon.Protocol.Conversation;

namespace Dmon.Memory.Tests.Index;

/// <summary>Task 4.3 — Flush barrier: RecordAsync + FlushAsync + SearchAsync round-trip.</summary>
public sealed class FlushBarrierTests : IAsyncLifetime
{
    private TempSessionFixture _fixture = null!;

    public Task InitializeAsync()
    {
        _fixture = TempSessionFixture.Create();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [Fact]
    public async Task FlushAsync_AfterRecord_SearchFindsRecordedTurns()
    {
        const string text  = "neural plasticity enables learning and memory consolidation";
        const string query = "neural plasticity memory";

        _fixture.EmbeddingGenerator.SetGroupVector("neuro-group",
            NomicEmbedding.ApplyDocumentPrefix(text),
            NomicEmbedding.ApplyQueryPrefix(query));

        (ShortTermMemory memory, string sessionIdA) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory memA = memory;

        await memA.RecordAsync([MakeRecord("user", text)]);

        // Explicit flush barrier (no-op with inline indexing, but must still make
        // the indexed turns searchable per the spec contract).
        await memA.FlushAsync();

        IReadOnlyList<MemoryHit> hits = await memA.SearchAsync(query);

        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Text.Contains("neural plasticity", StringComparison.OrdinalIgnoreCase));
        _ = sessionIdA;
    }

    [Fact]
    public async Task FlushAsync_MultipleRecords_AllAreSearchable()
    {
        const string text1 = "dopamine regulates reward pathways";
        const string text2 = "serotonin affects mood regulation";
        const string query  = "dopamine serotonin neurotransmitter";

        _fixture.EmbeddingGenerator.SetGroupVector("neurochem-group",
            NomicEmbedding.ApplyDocumentPrefix(text1),
            NomicEmbedding.ApplyDocumentPrefix(text2),
            NomicEmbedding.ApplyQueryPrefix(query));

        (ShortTermMemory memory, string sessionIdB) = await _fixture.BuildMemoryAsync();
        await using ShortTermMemory memB = memory;

        await memB.RecordAsync([
            MakeRecord("user",      text1),
            MakeRecord("assistant", text2),
        ]);

        await memB.FlushAsync();

        IReadOnlyList<MemoryHit> hits = await memB.SearchAsync(query, limit: 10);

        Assert.Contains(hits, h => h.Text == text1);
        Assert.Contains(hits, h => h.Text == text2);
        _ = sessionIdB;
    }

    private static MessageRecord MakeRecord(string role, string text) =>
        new()
        {
            EntryId   = Guid.NewGuid().ToString(),
            Timestamp = DateTimeOffset.UtcNow,
            Role      = role,
            Parts     = [new TextPart { Text = text }]
        };
}
