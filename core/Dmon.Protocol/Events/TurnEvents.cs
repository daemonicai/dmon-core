using System.Text.Json.Serialization;
using Dmon.Protocol.Enums;

namespace Dmon.Protocol.Events;

public sealed record TurnStartEvent : Event
{
}

public sealed record TurnEndEvent : Event
{
    [JsonPropertyName("message")]
    public required object Message { get; init; }

    [JsonPropertyName("toolResults")]
    public required IReadOnlyList<object> ToolResults { get; init; }
}

public sealed record MessageStartEvent : Event
{
    [JsonPropertyName("message")]
    public required object Message { get; init; }
}

public sealed record MessageDeltaEvent : Event
{
    [JsonPropertyName("message")]
    public required object Message { get; init; }

    [JsonPropertyName("delta")]
    public required object Delta { get; init; }
}

public sealed record MessageEndEvent : Event
{
    [JsonPropertyName("message")]
    public required object Message { get; init; }
}

public sealed record ToolExecutionStartEvent : Event
{
    [JsonPropertyName("callId")]
    public required string CallId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("args")]
    public required object Args { get; init; }
}

public sealed record ToolExecutionEndEvent : Event
{
    [JsonPropertyName("callId")]
    public required string CallId { get; init; }

    [JsonPropertyName("result")]
    public required object Result { get; init; }

    [JsonPropertyName("isError")]
    public bool IsError { get; init; }
}

public sealed record UiInputRequestEvent : Event
{
    [JsonPropertyName("id")]
    public required string EventId { get; init; }

    [JsonPropertyName("kind")]
    public UiInputKind Kind { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("options")]
    public IReadOnlyList<string>? Options { get; init; }
}