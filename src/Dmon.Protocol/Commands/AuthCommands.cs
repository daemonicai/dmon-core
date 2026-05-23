using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record AuthLoginCommand : Command
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

public sealed record AuthLogoutCommand : Command
{
    [JsonPropertyName("provider")]
    public required string Provider { get; init; }
}

public sealed record AuthStatusCommand : Command
{
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }
}