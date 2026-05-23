using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record ExtensionLoadCommand : Command
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

public sealed record ExtensionUnloadCommand : Command
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record ExtensionPromoteCommand : Command
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}