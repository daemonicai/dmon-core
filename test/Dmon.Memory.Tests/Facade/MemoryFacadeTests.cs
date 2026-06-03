using Dmon.Abstractions.Memory;
using Dmon.Memory.Tests.Facade.Fakes;
using Dmon.Protocol.Conversation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Memory.Tests.Facade;

/// <summary>
/// Unit tests for <see cref="Memory"/> — the IMemory facade.
/// All tiers are hand-written fakes; no Moq, no real storage.
/// </summary>
public sealed class MemoryFacadeTests
{
    // ── 3.1  Record / flush fan-out ─────────────────────────────────────────

    [Fact]
    public async Task RecordAsync_BothTiersPresent_InvokesBothWithSameRecordsAndScope()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();
        Memory memory = new(shortTerm, longTerm);

        List<MessageRecord> records = [MakeRecord("user", "hello")];

        await memory.RecordAsync(records, MemoryScope.Session);

        Assert.Equal(1, shortTerm.RecordCallCount);
        Assert.Equal(1, longTerm.RecordCallCount);
        Assert.Same(records, shortTerm.LastRecordedRecords);
        Assert.Same(records, longTerm.LastRecordedRecords);
        Assert.Equal(MemoryScope.Session, shortTerm.LastRecordedScope);
        Assert.Equal(MemoryScope.Session, longTerm.LastRecordedScope);
    }

    [Fact]
    public async Task FlushAsync_BothTiersPresent_InvokesBothFlushes()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();
        Memory memory = new(shortTerm, longTerm);

        await memory.FlushAsync();

        Assert.Equal(1, shortTerm.FlushCallCount);
        Assert.Equal(1, longTerm.FlushCallCount);
    }

    // ── 3.2  Fused search ───────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_PreservesSourceProvenance()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("st-1", "short-term hit one", MemorySource.ShortTerm, 0.9),
            new MemoryHit("st-2", "short-term hit two", MemorySource.ShortTerm, 0.8),
        ];
        longTerm.SearchResults =
        [
            new MemoryHit("lt-1", "long-term hit one", MemorySource.LongTerm, 0.95),
        ];

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("test");

        Assert.True(results.Count >= 3);

        // Every hit returned by the short-term fake must carry ShortTerm.
        foreach (MemoryHit hit in results.Where(h => h.Id.StartsWith("st", StringComparison.Ordinal)))
            Assert.Equal(MemorySource.ShortTerm, hit.Source);

        // Every hit returned by the long-term fake must carry LongTerm.
        foreach (MemoryHit hit in results.Where(h => h.Id.StartsWith("lt", StringComparison.Ordinal)))
            Assert.Equal(MemorySource.LongTerm, hit.Source);
    }

    [Fact]
    public async Task SearchAsync_RrfRankOrdering_NotRawScore()
    {
        // Construct a case where ordering by raw Score differs from RRF-rank order.
        //
        // Short-term returns (ordered, so rank-1 = index 0):
        //   rank 1: text "alpha", tiny raw score 0.01
        //   rank 2: text "beta",  big  raw score 0.99
        //
        // Long-term: empty.
        //
        // Expected fused order (by RRF):
        //   1st: "alpha"  — RRF = 1/(60+1) ≈ 0.01639
        //   2nd: "beta"   — RRF = 1/(60+2) ≈ 0.01613
        //
        // If the facade sorted by raw Score it would produce beta first (0.99 > 0.01).
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("alpha", "alpha", MemorySource.ShortTerm, 0.01),   // rank 1 (index 0)
            new MemoryHit("beta",  "beta",  MemorySource.ShortTerm, 0.99),   // rank 2 (index 1)
        ];
        longTerm.SearchResults = [];

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Equal(2, results.Count);
        Assert.Equal("alpha", results[0].Id);   // rank-1 ST hit comes first despite tiny raw score
        Assert.Equal("beta",  results[1].Id);

        // Fused scores follow RRF: rank-1 score > rank-2 score.
        Assert.True(results[0].Score > results[1].Score,
            $"rank-1 fused score {results[0].Score} should exceed rank-2 fused score {results[1].Score}");
    }

    [Fact]
    public async Task SearchAsync_CrossTierDuplicateAccumulates_OutranksEqualSingleTierHit()
    {
        // A hit that appears in BOTH tiers at rank 1 each accumulates:
        //   fused = 1/(60+1) + 1/(60+1) ≈ 0.03279
        //
        // A different hit that appears only in short-term at rank 1 scores:
        //   fused = 1/(60+1) ≈ 0.01639
        //
        // The cross-tier duplicate must outrank the single-tier hit with the same per-tier rank.
        const string sharedText = "shared knowledge";
        const string uniqueText = "unique knowledge";

        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("shared-st", sharedText, MemorySource.ShortTerm, 0.5),
            new MemoryHit("unique-st", uniqueText, MemorySource.ShortTerm, 0.4),
        ];
        longTerm.SearchResults =
        [
            new MemoryHit("shared-lt", sharedText, MemorySource.LongTerm, 0.5),
        ];

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        // Shared text is deduplicated to exactly one entry.
        int sharedCount = results.Count(r => r.Text == sharedText);
        Assert.Equal(1, sharedCount);

        // The survivor for the shared hit should outrank the unique single-tier hit.
        MemoryHit? shared = results.FirstOrDefault(r => r.Text == sharedText);
        MemoryHit? unique = results.FirstOrDefault(r => r.Text == uniqueText);
        Assert.NotNull(shared);
        Assert.NotNull(unique);
        Assert.True(shared.Score > unique.Score,
            $"Cross-tier accumulated score {shared.Score} should exceed single-tier score {unique.Score}");
    }

    [Fact]
    public async Task SearchAsync_CrossTierDuplicate_SurvivorCarriesRelations()
    {
        // When the same text appears in both tiers, the copy with Relations survives.
        // Long-term copy has a non-empty Relations list; short-term copy does not.
        const string sharedText = "relation bearing fact";

        List<MemoryRelation> relations = [new MemoryRelation("nodeA", "knows", "nodeB")];

        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("shared-st", sharedText, MemorySource.ShortTerm, 0.8, Relations: null),
        ];
        longTerm.SearchResults =
        [
            new MemoryHit("shared-lt", sharedText, MemorySource.LongTerm, 0.7, Relations: relations),
        ];

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        int count = results.Count(r => r.Text == sharedText);
        Assert.Equal(1, count);

        MemoryHit? survivor = results.FirstOrDefault(r => r.Text == sharedText);
        Assert.NotNull(survivor);
        Assert.NotNull(survivor.Relations);
        Assert.NotEmpty(survivor.Relations);
        Assert.Equal("knows", survivor.Relations[0].Relation);
    }

    [Fact]
    public async Task SearchAsync_OneTierEmpty_ShortTermOnly_ReturnsLongTermHits()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults = [];
        longTerm.SearchResults =
        [
            new MemoryHit("lt-1", "long-term only", MemorySource.LongTerm, 0.9),
        ];

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Single(results);
        Assert.Equal("lt-1", results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_OneTierEmpty_LongTermOnly_ReturnsShortTermHits()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("st-1", "short-term only", MemorySource.ShortTerm, 0.9),
        ];
        longTerm.SearchResults = [];

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Single(results);
        Assert.Equal("st-1", results[0].Id);
    }

    // ── 3.3  Long-term-disabled path ────────────────────────────────────────

    [Fact]
    public async Task LongTermNull_LongTermPropertyIsNull()
    {
        FakeShortTermMemory shortTerm = new();
        Memory memory = new(shortTerm);

        Assert.Null(memory.LongTerm);
    }

    [Fact]
    public async Task LongTermNull_RecordAsync_OnlyInvokesShortTerm()
    {
        FakeShortTermMemory shortTerm = new();
        Memory memory = new(shortTerm);

        List<MessageRecord> records = [MakeRecord("assistant", "reply")];
        await memory.RecordAsync(records, MemoryScope.User);

        Assert.Equal(1, shortTerm.RecordCallCount);
        Assert.Equal(MemoryScope.User, shortTerm.LastRecordedScope);
    }

    [Fact]
    public async Task LongTermNull_SearchAsync_ReturnsShortTermResults()
    {
        FakeShortTermMemory shortTerm = new();
        shortTerm.SearchResults =
        [
            new MemoryHit("st-1", "short only", MemorySource.ShortTerm, 0.8),
        ];

        Memory memory = new(shortTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Single(results);
        Assert.Equal("st-1", results[0].Id);
    }

    [Fact]
    public async Task LongTermNull_FlushAsync_OnlyInvokesShortTerm()
    {
        FakeShortTermMemory shortTerm = new();
        Memory memory = new(shortTerm);

        await memory.FlushAsync();

        Assert.Equal(1, shortTerm.FlushCallCount);
    }

    // ── 3.4  Resilience ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_LongTermThrows_DegradesToShortTermResults()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("st-1", "survived hit", MemorySource.ShortTerm, 0.9),
        ];
        longTerm.SearchOverride = (_, _, _, _) =>
            Task.FromException<IReadOnlyList<MemoryHit>>(new InvalidOperationException("long-term is down"));

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Single(results);
        Assert.Equal("st-1", results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_LongTermThrowsSynchronously_DegradesToShortTermResults()
    {
        // Covers the case where LongTerm.SearchAsync throws *before* returning a Task
        // (synchronous throw from the implementation). The facade must contain this and
        // return the short-term results, not propagate the exception.
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("st-1", "survived hit", MemorySource.ShortTerm, 0.9),
        ];
        longTerm.SearchOverride = (_, _, _, _) => throw new InvalidOperationException("sync boom");

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Single(results);
        Assert.Equal("st-1", results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_LongTermTimesOut_DegradesToShortTermResults()
    {
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchResults =
        [
            new MemoryHit("st-1", "timeout survivor", MemorySource.ShortTerm, 0.9),
        ];
        longTerm.SearchOverride = (_, _, _, _) =>
            Task.FromException<IReadOnlyList<MemoryHit>>(new TimeoutException("long-term timed out"));

        Memory memory = new(shortTerm, longTerm);
        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Single(results);
        Assert.Equal("st-1", results[0].Id);
    }

    [Fact]
    public async Task SearchAsync_ShortTermThrows_PropagatesException()
    {
        // A short-term fault must NOT be swallowed — it propagates to the caller.
        FakeShortTermMemory shortTerm = new();
        FakeLongTermMemory  longTerm  = new();

        shortTerm.SearchOverride = (_, _, _, _) =>
            Task.FromException<IReadOnlyList<MemoryHit>>(new InvalidOperationException("short-term is down"));
        longTerm.SearchResults =
        [
            new MemoryHit("lt-1", "long-term hit", MemorySource.LongTerm, 0.9),
        ];

        Memory memory = new(shortTerm, longTerm);
        await Assert.ThrowsAsync<InvalidOperationException>(() => memory.SearchAsync("q"));
    }

    // ── 3.5  DI integration ─────────────────────────────────────────────────

    [Fact]
    public void AddDmonMemory_RegistersDescriptorsForEmbeddingGeneratorAndShortTermMemory()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddDmonMemory();

        bool hasEmbeddingGen = services.Any(d =>
            d.ServiceType == typeof(IEmbeddingGenerator<string, Embedding<float>>));
        bool hasShortTermMemory = services.Any(d =>
            d.ServiceType == typeof(IShortTermMemory));
        bool hasMemory = services.Any(d =>
            d.ServiceType == typeof(IMemory));

        Assert.True(hasEmbeddingGen, "IEmbeddingGenerator<string,Embedding<float>> should be registered");
        Assert.True(hasShortTermMemory, "IShortTermMemory should be registered");
        Assert.True(hasMemory, "IMemory should be registered");
    }

    [Fact]
    public void AddDmonMemory_PreRegisteredShortTerm_ResolvesShortTermOnlyFacade()
    {
        // Pre-register a fake IShortTermMemory before calling AddDmonMemory.
        // TryAddSingleton must not clobber it.
        FakeShortTermMemory fakeShortTerm = new();

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IShortTermMemory>(fakeShortTerm);
        services.AddDmonMemory();

        ServiceProvider provider = services.BuildServiceProvider();
        IMemory memory = provider.GetRequiredService<IMemory>();

        Assert.IsType<Memory>(memory);
        Assert.Null(memory.LongTerm);
        Assert.Same(fakeShortTerm, memory.ShortTerm);
    }

    [Fact]
    public void AddDmonMemory_LongTermRegisteredBeforeAddDmonMemory_ResolvesFusedFacade()
    {
        // Long-term fake registered BEFORE AddDmonMemory; both orderings must produce a fused facade.
        FakeShortTermMemory fakeShortTerm = new();
        FakeLongTermMemory  fakeLongTerm  = new();

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IShortTermMemory>(fakeShortTerm);
        services.AddSingleton<ILongTermMemory>(fakeLongTerm);
        services.AddDmonMemory();

        ServiceProvider provider = services.BuildServiceProvider();
        IMemory memory = provider.GetRequiredService<IMemory>();

        Assert.IsType<Memory>(memory);
        Assert.NotNull(memory.LongTerm);
        Assert.Same(fakeShortTerm, memory.ShortTerm);
        Assert.Same(fakeLongTerm,  memory.LongTerm);
    }

    [Fact]
    public void AddDmonMemory_LongTermRegisteredAfterAddDmonMemory_ResolvesFusedFacade()
    {
        // Long-term fake registered AFTER AddDmonMemory; facade resolves via GetService at construction.
        FakeShortTermMemory fakeShortTerm = new();
        FakeLongTermMemory  fakeLongTerm  = new();

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IShortTermMemory>(fakeShortTerm);
        services.AddDmonMemory();
        services.AddSingleton<ILongTermMemory>(fakeLongTerm);   // registered AFTER

        ServiceProvider provider = services.BuildServiceProvider();
        IMemory memory = provider.GetRequiredService<IMemory>();

        Assert.IsType<Memory>(memory);
        Assert.NotNull(memory.LongTerm);
        Assert.Same(fakeShortTerm, memory.ShortTerm);
        Assert.Same(fakeLongTerm,  memory.LongTerm);
    }

    [Fact]
    public async Task AddDmonMemory_BothOrderings_FusedFacadeSearchesBothTiers()
    {
        // Verify that a facade resolved from DI actually fuses both tiers on search.
        FakeShortTermMemory fakeShortTerm = new();
        FakeLongTermMemory  fakeLongTerm  = new();

        fakeShortTerm.SearchResults =
        [
            new MemoryHit("st-1", "short hit", MemorySource.ShortTerm, 0.8),
        ];
        fakeLongTerm.SearchResults =
        [
            new MemoryHit("lt-1", "long hit", MemorySource.LongTerm, 0.7),
        ];

        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<IShortTermMemory>(fakeShortTerm);
        services.AddSingleton<ILongTermMemory>(fakeLongTerm);
        services.AddDmonMemory();

        ServiceProvider provider = services.BuildServiceProvider();
        IMemory memory = provider.GetRequiredService<IMemory>();

        IReadOnlyList<MemoryHit> results = await memory.SearchAsync("q");

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "st-1");
        Assert.Contains(results, r => r.Id == "lt-1");
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
