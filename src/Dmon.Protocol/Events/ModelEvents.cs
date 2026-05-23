using System.Text.Json.Serialization;
using Dmon.Protocol.Models;

namespace Dmon.Protocol.Events;

public sealed record ModelListResultEvent : Event
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<Model> Models { get; init; }

    [JsonPropertyName("activeProvider")]
    public required string ActiveProvider { get; init; }

    [JsonPropertyName("activeModelId")]
    public required string ActiveModelId { get; init; }
}
