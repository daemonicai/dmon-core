using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record ModelSetCommand : Command
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }

    [JsonPropertyName("modelId")]
    public required string ModelId { get; init; }
}

public sealed record ModelCycleCommand : Command
{
}

public sealed record ModelListCommand : Command
{
}

public sealed record ModelModelsCommand : Command
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}