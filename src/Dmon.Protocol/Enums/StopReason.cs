using System.Text.Json.Serialization;

namespace Dmon.Protocol.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StopReason
{
    Stop,
    Length,
    ToolUse,
    Error
}