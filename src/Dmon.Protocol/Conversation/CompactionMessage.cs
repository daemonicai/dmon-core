using System.Text.Json.Serialization;

namespace Dmon.Protocol.Conversation;

/// <summary>
/// Records a compaction event in the session log. The discriminator "compaction" is emitted by
/// the <see cref="SessionLogLine"/> polymorphic base — no manual Type property.
/// </summary>
public sealed record CompactionMessage : SessionLogLine
{
    [JsonPropertyName("entryId")]
    public required string EntryId { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("supersedesUpTo")]
    public required string SupersedesUpTo { get; init; }

    /// <summary>Valid values: "manual", "threshold", "overflow".</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("tokensBefore")]
    public long TokensBefore { get; init; }
}
