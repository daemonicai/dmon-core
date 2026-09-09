using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Sessions;

namespace Dmon.Protocol.Events;

public sealed record ToolConfirmRequestEvent : Event
{
    [JsonPropertyName("id")]
    public required string ConfirmId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("args")]
    public required object Args { get; init; }

    [JsonPropertyName("risk")]
    public RiskLevel Risk { get; init; }
}

public sealed record SessionUpdatedEvent : Event
{
    [JsonPropertyName("id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}

/// <summary>
/// Emitted when the agent core implicitly creates a session on first use (ADR-015 §2/§3).
/// This is a non-command notification: there is no originating command to correlate to, so
/// it derives directly from <see cref="Event"/>, not <see cref="ResultEvent"/>, and carries no
/// command <c>id</c>.
/// </summary>
public sealed record SessionStartedEvent : Event
{
    [JsonPropertyName("session")]
    public required SessionMeta Session { get; init; }
}

public sealed record CompactionStartEvent : Event
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

public sealed record CompactionEndEvent : Event
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("aborted")]
    public bool Aborted { get; init; }
}