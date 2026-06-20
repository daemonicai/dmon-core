namespace Dmail.Models;

public sealed record EmailListRequest
{
    public string? Since { get; init; }
    public string? From { get; init; }
    public string? Account { get; init; }
    public string? Labels { get; init; }
    public int MaxResults { get; init; } = 20;
    public int Page { get; init; }
}

public sealed record EmailListItem
{
    public uint Uid { get; init; }
    public string Subject { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string Date { get; init; } = string.Empty;
    public string? Labels { get; init; }
    public string? Preview { get; init; }
}

public sealed record EmailListResponse
{
    public EmailListItem[] Results { get; init; } = [];
    public int TotalCount { get; init; }
}
