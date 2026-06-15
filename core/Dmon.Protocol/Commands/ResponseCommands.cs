using System.Text.Json.Serialization;

namespace Dmon.Protocol.Commands;

public sealed record ToolConfirmResponseCommand : Command
{
    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; init; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; init; }

    /// <summary>
    /// Permission scope: "once" | "project" | "global" | null (denied).
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

public sealed record UiInputResponseCommand : Command
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; init; }
}