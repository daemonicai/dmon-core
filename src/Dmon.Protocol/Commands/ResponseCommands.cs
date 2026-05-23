using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record ToolConfirmResponseCommand : Command
{
    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; init; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; init; }
}

public sealed record UiInputResponseCommand : Command
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; init; }
}