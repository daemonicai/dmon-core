using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Delta;

/// <summary>
/// Base type for messageDelta payloads.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StartDelta), typeDiscriminator: "start")]
[JsonDerivedType(typeof(TextStartDelta), typeDiscriminator: "textStart")]
[JsonDerivedType(typeof(TextDeltaDelta), typeDiscriminator: "textDelta")]
[JsonDerivedType(typeof(TextEndDelta), typeDiscriminator: "textEnd")]
[JsonDerivedType(typeof(ThinkingStartDelta), typeDiscriminator: "thinkingStart")]
[JsonDerivedType(typeof(ThinkingDeltaDelta), typeDiscriminator: "thinkingDelta")]
[JsonDerivedType(typeof(ThinkingEndDelta), typeDiscriminator: "thinkingEnd")]
[JsonDerivedType(typeof(ToolCallStartDelta), typeDiscriminator: "toolCallStart")]
[JsonDerivedType(typeof(ToolCallDeltaDelta), typeDiscriminator: "toolCallDelta")]
[JsonDerivedType(typeof(ToolCallEndDelta), typeDiscriminator: "toolCallEnd")]
[JsonDerivedType(typeof(DoneDelta), typeDiscriminator: "done")]
[JsonDerivedType(typeof(ErrorDelta), typeDiscriminator: "error")]
public abstract record MessageDelta
{
}

public sealed record StartDelta : MessageDelta
{
}

public sealed record TextStartDelta : MessageDelta
{
}

public sealed record TextDeltaDelta : MessageDelta
{
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }

    [JsonPropertyName("partial")]
    public bool Partial { get; init; }
}

public sealed record TextEndDelta : MessageDelta
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

public sealed record ThinkingStartDelta : MessageDelta
{
}

public sealed record ThinkingDeltaDelta : MessageDelta
{
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed record ThinkingEndDelta : MessageDelta
{
    [JsonPropertyName("content")]
    public required string Content { get; init; }
}

public sealed record ToolCallStartDelta : MessageDelta
{
}

public sealed record ToolCallDeltaDelta : MessageDelta
{
    [JsonPropertyName("delta")]
    public required string Delta { get; init; }
}

public sealed record ToolCallEndDelta : MessageDelta
{
    [JsonPropertyName("toolCall")]
    public required object ToolCall { get; init; }
}

public sealed record DoneDelta : MessageDelta
{
    [JsonPropertyName("reason")]
    public StopReason Reason { get; init; }
}

public sealed record ErrorDelta : MessageDelta
{
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}