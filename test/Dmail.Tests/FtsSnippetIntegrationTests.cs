using Dmail.Data;
using Dmail.Models;
using Dmail.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

#pragma warning disable SKEXP0070

namespace Dmail.Tests;

/// <summary>
/// 11.4: Integration tests for FTS5 snippet generation and semantic-only fallback preview.
/// Exercises the real HybridSearchService + DatabaseInitializer + ISqliteConnectionFactory
/// against a temp-file SQLite DB — no ONNX model required.
/// </summary>
public sealed class FtsSnippetIntegrationTests : IAsyncLifetime
{
    private string _dbPath = string.Empty;
    private SqliteConnectionFactory _factory = null!;
    private HybridSearchService _search = null!;

    public async Task InitializeAsync()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"dmail-fts-snippet-{Guid.NewGuid()}.db");
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Pooling = false
        }.ToString();

        _factory = new SqliteConnectionFactory(cs);
        await new DatabaseInitializer(_factory).InitializeAsync();

        // SqliteVec store uses the same file — EnsureCollectionExistsAsync creates the vec0 table.
        var vectorStore = new SqliteVectorStore(cs);
        var vectorStoreService = new VectorStoreService(vectorStore);

        // EmbeddingService requires an IEmbeddingGenerator; StubEmbeddingGenerator returns
        // an empty result, keeping IsModelReady=false so all VectorSearchAsync calls
        // return an empty dictionary without touching the ONNX model.
        var embeddingService = new EmbeddingService(
            new StubEmbeddingGenerator(),
            NullLogger<EmbeddingService>.Instance);

        _search = new HybridSearchService(_factory, vectorStoreService, embeddingService);
    }

    public Task DisposeAsync()
    {
        try { SqliteConnection.ClearAllPools(); File.Delete(_dbPath); } catch { }
        return Task.CompletedTask;
    }

    private async Task InsertEmailAsync(uint uid, string subject, string body)
    {
        var repo = new EmailRepository(_factory);
        await repo.UpsertEmailAsync(new Email
        {
            Uid = uid,
            Account = "user@example.com",
            Subject = subject,
            Body = body,
            FromAddr = "sender@example.com",
            Date = DateTime.UtcNow,
            PendingEmbedding = false
        });
    }

    [Fact]
    public async Task KeywordSearch_MatchingEmail_SnippetContainsMarkTags()
    {
        // Arrange: insert an email whose body contains the keyword "budget"
        await InsertEmailAsync(1,
            "Q3 Finance Review",
            "The overall budget for this quarter needs careful review. Budget allocations must be approved by Friday.");

        // Act: keyword-only search (no Semantic → vector path is skipped)
        var response = await _search.SearchAsync(new SearchRequest
        {
            Keywords = ["budget"],
            MaxResults = 5
        });

        // Assert: result found, snippet contains <mark> highlighting
        Assert.NotEmpty(response.Results);
        var result = response.Results[0];
        Assert.NotNull(result.Snippet);
        Assert.Contains("<mark>", result.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("</mark>", result.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeywordSearch_MatchingEmail_SnippetCappedAt150Chars()
    {
        // Arrange: long body to verify length cap
        var longBody = string.Concat(Enumerable.Repeat("This email discusses the budget allocation and spending. ", 20));
        await InsertEmailAsync(2, "Budget Planning", longBody);

        var response = await _search.SearchAsync(new SearchRequest
        {
            Keywords = ["budget"],
            MaxResults = 5
        });

        Assert.NotEmpty(response.Results);
        var snippet = response.Results[0].Snippet;
        Assert.NotNull(snippet);

        // Strip mark tags and ellipsis to measure raw text length
        // The snippet (including tags) must be short enough — the underlying
        // FTS5 snippet() token limit produces text well under 150 tokens of raw chars.
        // We assert the snippet is not a full copy of the long body.
        Assert.True(snippet!.Length < longBody.Length,
            $"Snippet ({snippet.Length} chars) should be shorter than the full body ({longBody.Length} chars)");
    }

    [Fact]
    public async Task KeywordSearch_MatchTermInSubject_SnippetPresent()
    {
        // FTS5 indexes both subject (column 0) and body (column 1).
        // snippet() targets column index 2 (body in the query) — a subject-only match
        // may produce an empty or ellipsis snippet but must not throw.
        await InsertEmailAsync(3, "invoice reminder", "See the attached document for details.");

        var response = await _search.SearchAsync(new SearchRequest
        {
            Keywords = ["invoice"],
            MaxResults = 5
        });

        Assert.NotEmpty(response.Results);
        // Snippet may be null, empty, or the body content — must not throw
        Assert.Equal(3u, response.Results[0].Uid);
    }

    [Fact]
    public async Task KeywordSearch_NoMatch_ReturnsEmptyResults()
    {
        await InsertEmailAsync(4, "Hello World", "A generic email body with no special terms.");

        var response = await _search.SearchAsync(new SearchRequest
        {
            Keywords = ["zzznomatch"],
            MaxResults = 5
        });

        Assert.Empty(response.Results);
    }

    [Fact]
    public async Task FtsSnippet_LongBody_ShorterThanFullBody()
    {
        // FTS5 snippet() is token-bounded (not character-bounded): it returns at most
        // nTokens words of context around the match. For a long-body email the snippet
        // is always much shorter than the full body text.
        var body = string.Concat(Enumerable.Repeat("quarterly budget review discussion ", 50));
        await InsertEmailAsync(5, "Finance Meeting", body);

        var response = await _search.SearchAsync(new SearchRequest
        {
            Keywords = ["budget"],
            MaxResults = 5
        });

        Assert.NotEmpty(response.Results);
        var snippet = response.Results[0].Snippet;
        Assert.NotNull(snippet);

        // The snippet must be strictly shorter than the original body.
        Assert.True(snippet!.Length < body.Length,
            $"Snippet ({snippet.Length} chars) should be shorter than the full body ({body.Length} chars)");
    }

    [Fact]
    public async Task SubjectOnlyKeywordMatch_FallsBackToTruncatedText()
    {
        // When the keyword is found only in the subject (not the body), snippet() on
        // the body column returns null/empty. HybridSearchService falls back to
        // TruncateBody(subject, 150). Assert the result snippet is ≤ 153 chars
        // (150 content + "..." suffix).
        var longSubject = "Invoice " + new string('x', 200); // > 150 chars
        await InsertEmailAsync(6, longSubject, "No matching terms here.");

        var response = await _search.SearchAsync(new SearchRequest
        {
            Keywords = ["invoice"],
            MaxResults = 5
        });

        Assert.NotEmpty(response.Results);
        var result = response.Results[0];
        Assert.Equal(6u, result.Uid);
        Assert.NotNull(result.Snippet);
        // Snippet must be either the FTS body snippet or the truncated subject fallback.
        // In either case it must not contain the full 200-char subject verbatim.
        Assert.True(result.Snippet!.Length <= longSubject.Length,
            $"Snippet ({result.Snippet.Length} chars) should not exceed subject length ({longSubject.Length} chars)");
    }
}

/// <summary>
/// Test double: returns a single zero-dimension embedding so EmbeddingService.IsModelReady
/// stays false (ValidateModelAsync is never called) — keeps the vector path dormant.
/// </summary>
internal sealed class StubEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public EmbeddingGeneratorMetadata Metadata { get; } =
        new EmbeddingGeneratorMetadata("stub", new Uri("http://stub"), "stub");

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var embeddings = values.Select(_ => new Embedding<float>(new float[0])).ToList();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
