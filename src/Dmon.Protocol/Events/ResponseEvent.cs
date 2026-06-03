using System.Text.Json.Serialization;

namespace Dmon.Protocol.Events;

/// <summary>
/// <para>
/// <b>Transitional — success path only:</b> this type is quarantined to the
/// <c>session.getMessages</c> <em>success</em> response while conversation-persistence work
/// (turn-persistence change) is pending. All other commands, and all failure paths (including
/// <c>session.getMessages</c> failure), emit <see cref="CommandErrorEvent"/> or typed
/// <see cref="ResultEvent"/> subtypes. Once turn-persistence lands and
/// <c>session.getMessages</c> is retired, this record and its <c>"response"</c> discriminator
/// will be removed from the <see cref="Event"/> polymorphic table.
/// </para>
/// Response envelope for <c>session.getMessages</c> success. Matches the shape:
/// {"id": "req-1", "type": "response", "command": "session.getMessages", "success": true, "data": {...}}
/// </summary>
public sealed record ResponseEvent : Event
{
    [JsonPropertyName("id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("command")]
    public required string Command { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
