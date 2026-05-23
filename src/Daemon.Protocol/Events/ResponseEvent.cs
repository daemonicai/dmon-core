using System.Text.Json.Serialization;

namespace Daemon.Protocol.Events;

/// <summary>
/// Response envelope for command requests. Matches the shape:
/// {"id": "req-1", "type": "response", "command": "...", "success": true, "data": {...}}
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
