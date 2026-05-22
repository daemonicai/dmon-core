using System.Text.Json.Serialization;

namespace Daemon.Core.Session;

public sealed record SessionMeta
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("created")]
    public DateTimeOffset Created { get; init; }

    [JsonPropertyName("modified")]
    public DateTimeOffset Modified { get; init; }

    [JsonPropertyName("parentSession")]
    public string? ParentSession { get; init; }

    [JsonPropertyName("forkEntryId")]
    public string? ForkEntryId { get; init; }

    [JsonPropertyName("tokens")]
    public SessionTokens Tokens { get; init; } = new();

    [JsonPropertyName("cost")]
    public SessionCost Cost { get; init; } = new();
}

public sealed record SessionTokens
{
    [JsonPropertyName("input")]
    public long Input { get; init; }

    [JsonPropertyName("output")]
    public long Output { get; init; }

    [JsonPropertyName("cacheRead")]
    public long CacheRead { get; init; }

    [JsonPropertyName("cacheWrite")]
    public long CacheWrite { get; init; }
}

public sealed record SessionCost
{
    [JsonPropertyName("total")]
    public decimal Total { get; init; }
}
