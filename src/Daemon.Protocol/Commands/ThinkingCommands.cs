using System.Text.Json.Serialization;
using Daemon.Protocol.Enums;

namespace Daemon.Protocol.Commands;

public sealed record ThinkingSetCommand : Command
{
    [JsonPropertyName("level")]
    public ThinkingLevel Level { get; init; }
}

public sealed record ThinkingCycleCommand : Command
{
}