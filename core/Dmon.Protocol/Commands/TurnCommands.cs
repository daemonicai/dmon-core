using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record TurnSubmitCommand : Command
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<string>? Images { get; init; }
}

public sealed record TurnSteerCommand : Command
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<string>? Images { get; init; }
}

public sealed record TurnFollowUpCommand : Command
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("images")]
    public IReadOnlyList<string>? Images { get; init; }
}

public sealed record TurnAbortCommand : Command
{
}