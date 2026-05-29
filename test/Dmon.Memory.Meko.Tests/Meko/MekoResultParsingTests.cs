using System.Text.Json;
using Dmon.Abstractions.Memory;
using Dmon.Memory.Meko;

namespace Dmon.Memory.Meko.Tests.Meko;

/// <summary>
/// 4.2 — Defensive parsing of Meko tool results.
/// Real envelopes captured live 2026-05-29:
///   search/get_all: { "results": [...], "relations": [...], "promoted_relations": [...] }
///   memory_add:     { "results": [...], "relations": { "added_entities": ... } }
/// Well-formed → mapped <see cref="MemoryHit"/> with Source=LongTerm, Relations, Metadata.
/// Partial/malformed/empty → degrades (empty or recognized-only), never throws.
/// </summary>
public sealed class MekoResultParsingTests
{
    // --- search-path (results + top-level array relations) ---

    [Fact]
    public async Task SearchAsync_RealEnvelope_MapsToMemoryHits()
    {
        // Real memory_search envelope shape (live-captured 2026-05-29).
        const string json = """
            {
                "results": [
                    {
                        "id": "hit-1",
                        "memory": "The capital of France is Paris",
                        "score": 0.95,
                        "metadata": { "tag": "geography" }
                    }
                ],
                "relations": [],
                "promoted_relations": []
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
    public async Task SearchAsync_TopLevelArrayRelations_AttachedToHits()
    {
        // relations is a top-level array on the search path (not per-item).
        const string json = """
            {
                "results": [
                    { "id": "h1", "memory": "text about Alice and Bob" },
                    { "id": "h2", "memory": "more context" }
                ],
                "relations": [
                    { "source": "Alice", "relationship": "knows", "target": "Bob" },
                    { "source": "Bob", "relationship": "part_of", "target": "TeamC" }
                ],
                "promoted_relations": []
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Equal(2, hits.Count);
        // Both hits receive the same top-level relations.
        foreach (MemoryHit hit in hits)
        {
            Assert.NotNull(hit.Relations);
            Assert.Equal(2, hit.Relations!.Count);
        }

        MemoryRelation first = hits[0].Relations![0];
        Assert.Equal("Alice", first.Source);
        Assert.Equal("knows", first.Relation);
        Assert.Equal("Bob", first.Target);

        MemoryRelation second = hits[0].Relations![1];
        Assert.Equal("Bob", second.Source);
        Assert.Equal("part_of", second.Relation);
        Assert.Equal("TeamC", second.Target);
    }

    [Fact]
    public async Task SearchAsync_EmptyTopLevelRelationsArray_HitsHaveNoRelations()
    {
        // Empty relations array → Relations=null on hits (not empty list).
        const string json = """
            { "results": [ { "id": "h1", "memory": "text" } ], "relations": [] }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        MemoryHit hit = Assert.Single(hits);
        Assert.Null(hit.Relations);
    }

    [Fact]
    public async Task SearchAsync_RelationWithMissingRelationshipField_SkipsRelation()
    {
        // Top-level relations: one incomplete (missing "relationship"), one complete.
        const string json = """
            {
                "results": [ { "id": "h1", "memory": "text" } ],
                "relations": [
                    { "source": "A", "target": "B" },
                    { "source": "X", "relationship": "r", "target": "Y" }
                ]
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

    // --- memory_add path (object relations — must not throw) ---

    [Fact]
    public async Task SearchAsync_MemoryAddEnvelope_ObjectRelations_ReturnsHits_DoesNotThrow()
    {
        // memory_add returns relations as an object (not array) — parser must handle gracefully.
        const string json = """
            {
                "results": [ { "id": "abc-123", "memory": "stored fact", "event": "ADD" } ],
                "relations": {
                    "added_entities": [
                        [{ "source": "A", "relationship": "knows", "target": "B",
                           "source_id": "s1", "destination_id": "d1" }]
                    ],
                    "graph_nodes": [],
                    "deleted_entities": []
                }
            }
            """;

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        // Use SearchAsync as the parser entry point (same code path as any hits parse).
        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Single(hits);
        Assert.Equal("abc-123", hits[0].Id);
        Assert.Equal("stored fact", hits[0].Text);
        // Object relations are silently ignored — no Relations on hits.
        Assert.Null(hits[0].Relations);
    }

    // --- empty envelope ---

    [Fact]
    public async Task SearchAsync_EmptyResultsArray_ReturnsEmpty_DoesNotThrow()
    {
        // Real empty search response.
        const string json = """{ "results": [], "relations": [] }""";

        var fake = new FakeMekoToolInvoker();
        fake.EnqueueJsonResult(json);
        var memory = MekoTestHelpers.BuildMemory(fake);

        IReadOnlyList<MemoryHit> hits = await memory.SearchAsync("q");

        Assert.Empty(hits);
    }

    // --- metadata ---

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
                ],
                "relations": []
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

    // --- fallback text field ---

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

    // --- malformed / missing ---

    [Fact]
    public async Task SearchAsync_EmptyJsonObject_ReturnsEmpty_DoesNotThrow()
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
                ],
                "relations": []
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

    // --- GetAsync / ParseSingleHit ---

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
}
