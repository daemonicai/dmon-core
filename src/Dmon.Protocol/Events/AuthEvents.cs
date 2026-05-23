using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// Auth-related events per ADR-005.
/// </summary>
public sealed record AuthLoginCompleteEvent : Event
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

public sealed record AuthLogoutCompleteEvent : Event
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

public sealed record AuthLoginFailedEvent : Event
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record AuthStatusResultEvent : Event
{
    [JsonPropertyName("providers")]
    public required IReadOnlyList<object> Providers { get; init; }
}