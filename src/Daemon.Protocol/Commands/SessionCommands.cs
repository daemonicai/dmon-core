using System.Text.Json.Serialization;

namespace Daemon.Protocol.Commands;

public sealed record SessionCreateCommand : Command
{
}

public sealed record SessionForkCommand : Command
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }
}

public sealed record SessionCloneCommand : Command
{
}

public sealed record SessionLoadCommand : Command
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }
}

public sealed record SessionListCommand : Command
{
}

public sealed record SessionSetNameCommand : Command
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record SessionGetStatsCommand : Command
{
}

public sealed record SessionGetMessagesCommand : Command
{
}

public sealed record SessionCompactCommand : Command
{
}