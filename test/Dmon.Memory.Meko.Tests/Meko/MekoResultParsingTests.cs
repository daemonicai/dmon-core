using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.2 — Defensive parsing of Meko tool results (D13).
/// Well-formed → mapped <see cref="MemoryHit"/> with Source=LongTerm, Relations, Metadata.
/// Partial/malformed/empty → degrades (empty or recognized-only), never throws.
/// </summary>
public sealed class MekoResultParsingTests
{
    [Fact]
    public async Task SearchAsync_WellFormedResult_MapsToMemoryHits()
    {
        const string json = """
            {
                "results": [
                    {
                        "id": "hit-1",
                        "memory": "The capital of France is Paris",
                        "score": 0.95,
                        "metadata": { "tag": "geography" },
                        "relations": [
                            { "source": "Paris", "relation": "capital_of", "target": "France" }
                        ]
                    }
                ]
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("capital of France");

        Assert.Single(hits);
        MemoryHit hit = hits[0];
        Assert.Equal("hit-1", hit.Id);
        Assert.Equal("The capital of France is Paris", hit.Text);
        Assert.Equal(MemorySource.LongTerm, hit.Source);
        Assert.Equal(0.95, hit.Score, precision: 3);
    }

    [Fact]
    public async Task SearchAsync_WellFormedResult_MapsRelations()
    {
        const string json = """
            {
                "results": [
                    {
                        "id": "h1", "memory": "text",
                        "relations": [
                            { "source": "A", "relation": "knows", "target": "B" },
                            { "source": "B", "relation": "part_of", "target": "C" }
                        ]
                    }
                ]
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        MemoryHit hit = Assert.Single(hits);
        Assert.NotNull(hit.Relations);
        Assert.Equal(2, hit.Relations!.Count);
        Assert.Equal("A", hit.Relations[0].Source);
        Assert.Equal("knows", hit.Relations[0].Relation);
        Assert.Equal("B", hit.Relations[0].Target);
    }

    [Fact]
    public async Task SearchAsync_WellFormedResult_MapsMetadata()
    {
        const string json = """
            {
                "results": [
                    {
                        "id": "h1", "memory": "text",
                        "metadata": { "created_at": "2025-01-01", "confidence": 0.8 }
                    }
                ]
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        MemoryHit hit = Assert.Single(hits);
        Assert.NotNull(hit.Metadata);
        Assert.True(hit.Metadata!.ContainsKey("created_at"));
        Assert.Equal(JsonValueKind.String, hit.Metadata["created_at"].ValueKind);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToTextField_WhenMemoryFieldAbsent()
    {
        const string json = """{ "results": [{ "id": "h1", "text": "fallback text" }] }""";

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        MemoryHit hit = Assert.Single(hits);
        Assert.Equal("fallback text", hit.Text);
    }

    [Fact]
    public async Task SearchAsync_EmptyJson_ReturnsEmpty_DoesNotThrow()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{}");
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_MalformedJson_ReturnsEmpty_DoesNotThrow()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("{ not valid json !!!");
        var memory = MekoTestHelpers.BuildMemory(fake);

        // Must not throw, must return empty.
        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_EmptyContentBlock_ReturnsEmpty_DoesNotThrow()
    {
        // Result with no text blocks at all.
        var fake = new FakeMekoToolInvoker();
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_MissingIdInHit_SkipsHit_DoesNotThrow()
    {
        // Hit without an id should be skipped; other valid hits still returned.
        const string json = """
            {
                "results": [
                    { "memory": "no id here" },
                    { "id": "ok", "memory": "valid hit" }
                ]
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Single(hits);
        Assert.Equal("ok", hits[0].Id);
    }

    [Fact]
    public async Task SearchAsync_BareArray_ParsesHits()
    {
        const string json = """[{"id":"a1","memory":"bare array hit"}]""";

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Single(hits);
        Assert.Equal("a1", hits[0].Id);
    }

    [Fact]
    public async Task GetAsync_WellFormedSingleHit_IsMapped()
    {
        const string json = """{"id":"g1","memory":"single fact","score":0.75}""";

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        MemoryHit? hit = await memory.GetAsync("g1");

        Assert.NotNull(hit);
        Assert.Equal("g1", hit!.Id);
        Assert.Equal("single fact", hit.Text);
        Assert.Equal(MemorySource.LongTerm, hit.Source);
    }

    [Fact]
    public async Task GetAsync_MalformedJson_ReturnsNull_DoesNotThrow()
    {
        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult("not json");
        var memory = MekoTestHelpers.BuildMemory(fake);

        MemoryHit? hit = await memory.GetAsync("any-id");

        Assert.Null(hit);
    }

    [Fact]
    public async Task SearchAsync_RelationWithMissingField_SkipsRelation()
    {
        const string json = """
            {
                "results": [{
                    "id": "h1", "memory": "text",
                    "relations": [
                        { "source": "A", "target": "B" },
                        { "source": "X", "relation": "r", "target": "Y" }
                    ]
                }]
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        MemoryHit hit = Assert.Single(hits);
        // Only the complete relation should be present.
        Assert.NotNull(hit.Relations);
        Assert.Single(hit.Relations!);
        Assert.Equal("X", hit.Relations[0].Source);
    }
}
