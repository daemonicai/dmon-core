using System.Text.Json.Serialization;

namespace Dmon.Protocol.Sessions;

/// <summary>
/// Lightweight stats summary for a session, returned by <c>session.getStats</c>.
/// Distinct from <see cref="SessionTokens"/> / <see cref="SessionCost"/> on <see cref="SessionMeta"/>
/// — those are per-field accounting; this record is the protocol-level response payload.
/// </summary>
public sealed record SessionStats
{
    [JsonPropertyName("tokens")]
    public long Tokens { get; init; }

    [JsonPropertyName("cost")]
    public decimal Cost { get; init; }

    [JsonPropertyName("contextUsage")]
    public int ContextUsage { get; init; }

    [JsonPropertyName("currentModel")]
    public string? CurrentModel { get; init; }
}
