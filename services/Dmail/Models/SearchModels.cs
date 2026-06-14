namespace Daemonic.Dmail.Models;

public sealed record SearchRequest
{
    public string[]? Keywords { get; init; }
    public string? Semantic { get; init; }
    public string? From { get; init; }
    public string? Since { get; init; }
    public string? Account { get; init; }
    public int MaxResults { get; init; } = 10;
}

public sealed record SearchResult
{
    public uint Uid { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string? Snippet { get; init; }
    public string MatchType { get; init; } = string.Empty;
    public float? FtsScore { get; init; }
    public float? VectorScore { get; init; }
    public float? HybridScore { get; init; }
}

public sealed record SearchResponse
{
    public SearchResult[] Results { get; init; } = [];
    public int TotalFound { get; init; }
}
