using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dmon.Protocol.Conversation;

/// <summary>
/// Discriminated union of all content part types that may appear within a <see cref="MessageRecord"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), typeDiscriminator: "text")]
[JsonDerivedType(typeof(ToolCallPart), typeDiscriminator: "toolCall")]
[JsonDerivedType(typeof(ToolResultPart), typeDiscriminator: "toolResult")]
[JsonDerivedType(typeof(ImagePart), typeDiscriminator: "image")]
[JsonDerivedType(typeof(ReasoningPart), typeDiscriminator: "reasoning")]
[JsonDerivedType(typeof(UsagePart), typeDiscriminator: "usage")]
[JsonDerivedType(typeof(UnknownPart), typeDiscriminator: "unknown")]
public abstract record Part
{
}

public sealed record TextPart : Part
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record ToolCallPart : Part
{
    [JsonPropertyName("callId")]
    public required string CallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Free-form tool arguments as supplied by the model. Shape is tool-defined.</summary>
    [JsonPropertyName("args")]
    public required JsonElement Args { get; init; }
}

public sealed record ToolResultPart : Part
{
    [JsonPropertyName("callId")]
    public required string CallId { get; init; }

    /// <summary>Serialised tool result. Null when the result was offloaded to an attachment.</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }

    /// <summary>Session-relative path to the attachment file when the result was offloaded.</summary>
    [JsonPropertyName("attachmentRef")]
    public string? AttachmentRef { get; init; }

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}

public sealed record ImagePart : Part
{
    [JsonPropertyName("mediaType")]
    public required string MediaType { get; init; }

    /// <summary>Session-relative path to the attachment file when the image was offloaded.</summary>
    [JsonPropertyName("attachmentRef")]
    public string? AttachmentRef { get; init; }

    /// <summary>Base-64-encoded image bytes when stored inline.</summary>
    [JsonPropertyName("dataBase64")]
    public string? DataBase64 { get; init; }
}

public sealed record ReasoningPart : Part
{
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

public sealed record UsagePart : Part
{
    [JsonPropertyName("inputTokens")]
    public long InputTokens { get; init; }

    [JsonPropertyName("outputTokens")]
    public long OutputTokens { get; init; }
}

/// <summary>
/// Preserves content that could not be mapped to a known part type. The raw JSON is retained
/// verbatim so no information is lost at write-time; render-only (never replayed to the model).
/// </summary>
public sealed record UnknownPart : Part
{
    /// <summary>Original JSON of the unrecognised content item.</summary>
    [JsonPropertyName("raw")]
    public required JsonElement Raw { get; init; }

    /// <summary>Identifier of the component that produced this part (e.g. provider adapter name).</summary>
    [JsonPropertyName("producedBy")]
    public required string ProducedBy { get; init; }
}
