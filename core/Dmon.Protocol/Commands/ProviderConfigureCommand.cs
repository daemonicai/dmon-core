using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record ProviderConfigureCommand : Command
{
    [JsonPropertyName("adapter")]
    public required string Adapter { get; init; }

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }

    [JsonPropertyName("envVar")]
    public required string EnvVar { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }
}
