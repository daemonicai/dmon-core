using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Commands;

public sealed record ThinkingSetCommand : Command
{
    [JsonPropertyName("level")]
    public ThinkingLevel Level { get; init; }
}

public sealed record ThinkingCycleCommand : Command
{
}