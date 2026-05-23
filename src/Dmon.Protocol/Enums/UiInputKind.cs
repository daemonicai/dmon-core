using System.Text.Json.Serialization;

namespace Dmon.Protocol.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UiInputKind
{
    Text,
    Secret,
    Select
}