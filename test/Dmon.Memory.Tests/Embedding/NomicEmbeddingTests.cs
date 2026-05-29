using Dmon.Memory.Embedding;
using Microsoft.Extensions.AI;

namespace Dmon.Memory.Tests.Embedding;

public sealed class NomicEmbeddingTests
{
    [Theory]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("search_document: already prefixed")]
    public void ApplyDocumentPrefix_PrependsCorrently(string text)
    {
        string result = NomicEmbedding.ApplyDocumentPrefix(text);
        Assert.StartsWith("search_document: ", result, StringComparison.Ordinal);
        Assert.Equal(NomicEmbedding.DocumentPrefix + text, result);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("search_query: already prefixed")]
    public void ApplyQueryPrefix_PrependsCorrently(string text)
    {
        string result = NomicEmbedding.ApplyQueryPrefix(text);
        Assert.StartsWith("search_query: ", result, StringComparison.Ordinal);
        Assert.Equal(NomicEmbedding.QueryPrefix + text, result);
    }

    [Fact]
    public void DocumentAndQueryPrefixes_AreDifferent()
    {
        // The two prefixes must be distinct so document and query embeddings differ.
        Assert.NotEqual(NomicEmbedding.DocumentPrefix, NomicEmbedding.QueryPrefix);
    }

    [Fact]
    public async Task StubGenerator_SeesAlreadyPrefixedInput_DocumentPrefix()
    {
        // Verify that the generator receives the already-prefixed string
        // (prefixing happens at the call site, not inside the generator).
        var seen = new List<string>();
        var generator = new CapturingGenerator(seen);

        string text = "the quick brown fox";
        string prefixed = NomicEmbedding.ApplyDocumentPrefix(text);
        await generator.GenerateAsync([prefixed]);

        Assert.Single(seen);
        Assert.Equal(prefixed, seen[0]);
        Assert.StartsWith("search_document: ", seen[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StubGenerator_SeesAlreadyPrefixedInput_QueryPrefix()
    {
        var seen = new List<string>();
        var generator = new CapturingGenerator(seen);

        string text = "the quick brown fox";
        string prefixed = NomicEmbedding.ApplyQueryPrefix(text);
        await generator.GenerateAsync([prefixed]);

        Assert.Single(seen);
        Assert.Equal(prefixed, seen[0]);
        Assert.StartsWith("search_query: ", seen[0], StringComparison.Ordinal);
    }

    // ── Skippable integration test ───────────────────────────────────────────

    [Fact(Skip = "Integration test — requires the GGUF model file. Remove Skip to run manually with the model present.")]
    public async Task LocalEmbeddingGenerator_SameInputTwice_ReturnsDeterministicUnitNormVectors()
    {
        ModelResolver resolver = new();
        using LocalEmbeddingGenerator gen = new(resolver);
        const string text = "search_document: the quick brown fox";

        GeneratedEmbeddings<Embedding<float>> first  = await gen.GenerateAsync([text]);
        GeneratedEmbeddings<Embedding<float>> second = await gen.GenerateAsync([text]);

        float[] v1 = first[0].Vector.ToArray();
        float[] v2 = second[0].Vector.ToArray();

        // Same input → identical output.
        for (int i = 0; i < v1.Length; i++)
            Assert.Equal(v1[i], v2[i], precision: 5);

        // Output must be unit-norm.
        float norm = MathF.Sqrt(v1.Sum(x => x * x));
        Assert.Equal(1f, norm, precision: 4);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class CapturingGenerator(List<string> seen)
        : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("capturing", null, "capturing", 4);

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            string[] inputs = values as string[] ?? values.ToArray();
            seen.AddRange(inputs);
            Embedding<float>[] results = inputs
                .Select(_ => new Embedding<float>(new float[] { 0.5f, 0.5f, 0.5f, 0.5f }))
                .ToArray();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(results));
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            if (serviceKey is not null)
                return null;
            if (serviceType == typeof(EmbeddingGeneratorMetadata))
                return Metadata;
            if (serviceType.IsInstanceOfType(this))
                return this;
            return null;
        }

        public void Dispose() { }
    }
}

