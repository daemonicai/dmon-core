using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// Emitted when a command fails. Correlates to the originating command via <see cref="ResultEvent.CommandId"/>.
/// </summary>
public sealed record CommandErrorEvent : ResultEvent
{
    /// <summary>The command type that failed (e.g. <c>session.fork</c>).</summary>
    [JsonPropertyName("command")]
    public required string Command { get; init; }

    /// <summary>A machine-readable error code (e.g. <c>noActiveSession</c>).</summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>A human-readable description of the failure.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
