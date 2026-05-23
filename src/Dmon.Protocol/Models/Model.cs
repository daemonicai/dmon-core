using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Models;

/// <summary>
/// Provider model capability metadata returned by <c>model.list</c>.
/// </summary>
public sealed record Model
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("reasoning")]
    public bool Reasoning { get; init; }

    [JsonPropertyName("input")]
    public required IReadOnlyList<InputType> Input { get; init; }

    [JsonPropertyName("toolCalling")]
    public bool ToolCalling { get; init; }

    [JsonPropertyName("contextWindow")]
    public int ContextWindow { get; init; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; init; }
}