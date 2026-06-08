using System.Text.Json.Serialization;

namespace Dmon.Protocol.Conversation;

/// <summary>
/// A single conversational turn (user, assistant, tool, or system) written to the session log.
/// </summary>
public sealed record MessageRecord : SessionLogLine
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Role string: "user", "assistant", "system", or "tool".</summary>
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("parts")]
    public required IReadOnlyList<Part> Parts { get; init; }
}
