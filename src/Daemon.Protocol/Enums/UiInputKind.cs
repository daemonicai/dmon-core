using System.Text.Json.Serialization;

namespace Daemon.Protocol.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UiInputKind
{
    Text,
    Secret,
    Select
}