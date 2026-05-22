using System.Text.Json.Serialization;

namespace Daemon.Protocol.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InputType
{
    Text,
    Image
}