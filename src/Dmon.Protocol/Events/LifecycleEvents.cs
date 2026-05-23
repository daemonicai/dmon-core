using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// First event emitted on startup, before any command is processed.
/// </summary>
public sealed record AgentReadyEvent : Event
{
    [JsonPropertyName("protocolVersion")]
    public required string ProtocolVersion { get; init; }

    [JsonPropertyName("coreVersion")]
    public required string CoreVersion { get; init; }
}

public sealed record AgentStartEvent : Event
{
}

public sealed record AgentEndEvent : Event
{
    [JsonPropertyName("messages")]
    public required object[] Messages { get; init; }
}