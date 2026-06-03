using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// Auth-related events per ADR-005.
/// CommandId is required by ResultEvent; auth handlers are stubs today —
/// populate when emit sites are implemented.
/// </summary>
public sealed record AuthLoginCompleteEvent : ResultEvent
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

public sealed record AuthLogoutCompleteEvent : ResultEvent
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

public sealed record AuthLoginFailedEvent : ResultEvent
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record AuthStatusResultEvent : ResultEvent
{
    [JsonPropertyName("providers")]
    public required IReadOnlyList<object> Providers { get; init; }
}