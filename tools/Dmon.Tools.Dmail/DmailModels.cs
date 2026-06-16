namespace Dmon.Tools.Dmail;

// Wire DTOs mirroring the Dmail HTTP API (src/Dmail/Models/*). The API uses
// ASP.NET minimal-API web defaults, so JSON is camelCase and read case-insensitively.

internal sealed record SearchRequest
{
    public string[]? Keywords { get; init; }
    public string? Semantic { get; init; }
    public string? From { get; init; }
    public string? Since { get; init; }
    public string? Account { get; init; }
    public int MaxResults { get; init; } = 10;
}

internal sealed record SearchResult
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

internal sealed record SearchResponse
{
    public SearchResult[] Results { get; init; } = [];
    public int TotalFound { get; init; }
}

internal sealed record EmailListRequest
{
    public string? Since { get; init; }
    public string? From { get; init; }
    public string? Account { get; init; }
    public string? Labels { get; init; }
    public int MaxResults { get; init; } = 20;
    public int Page { get; init; }
}

internal sealed record EmailListItem
{
    public uint Uid { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string? Labels { get; init; }
    public string? Preview { get; init; }
}

internal sealed record EmailListResponse
{
    public EmailListItem[] Results { get; init; } = [];
    public int TotalCount { get; init; }
}

internal sealed record EmailDetail
{
    public long Uid { get; init; }
    public string Account { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string? Labels { get; init; }
}
