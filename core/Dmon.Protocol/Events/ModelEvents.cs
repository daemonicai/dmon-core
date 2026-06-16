using System.Text.Json.Serialization;
using Dmon.Protocol.Models;

namespace Dmon.Protocol.Events;

public sealed record ModelListResultEvent : ResultEvent
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<Model> Models { get; init; }

    [JsonPropertyName("activeProvider")]
    public required string ActiveProvider { get; init; }

    [JsonPropertyName("activeModelId")]
    public required string ActiveModelId { get; init; }
}

public sealed record ModelModelsResultEvent : ResultEvent
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("models")]
    public required IReadOnlyList<string> Models { get; init; }

    [JsonPropertyName("activeModelId")]
    public string? ActiveModelId { get; init; }
}
