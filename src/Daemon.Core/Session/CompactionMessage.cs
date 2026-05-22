using System.Text.Json.Serialization;

namespace Daemon.Core.Session;

public sealed record CompactionMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "compaction";

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
