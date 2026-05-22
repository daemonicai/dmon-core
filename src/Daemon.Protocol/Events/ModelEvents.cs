using System.Text.Json.Serialization;
using Daemon.Protocol.Models;

namespace Daemon.Protocol.Events;

public sealed record ModelListResultEvent : Event
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<Model> Models { get; init; }

    [JsonPropertyName("activeProvider")]
    public required string ActiveProvider { get; init; }

    [JsonPropertyName("activeModelId")]
    public required string ActiveModelId { get; init; }
}
