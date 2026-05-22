using System.Text.Json.Serialization;

namespace Daemon.Protocol.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StopReason
{
    Stop,
    Length,
    ToolUse,
    Error
}