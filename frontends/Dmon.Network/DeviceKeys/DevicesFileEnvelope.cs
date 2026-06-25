using System.Text.Json.Serialization;

namespace Dmon.Network.DeviceKeys;

/// <summary>
/// STJ DTO for the <c>devices.json</c> root envelope.
/// Unknown fields are ignored (lenient parse) to accommodate future additions such as <c>expiresAt</c>.
/// </summary>
internal sealed class DevicesFileEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("devices")]
    public IReadOnlyList<DeviceEntryDto> Devices { get; init; } = [];
}

/// <summary>
/// STJ DTO for a single entry in the <c>devices</c> array.
/// </summary>
internal sealed class DeviceEntryDto
{
    [JsonPropertyName("keyId")]
    public string KeyId { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("secretHash")]
    public string SecretHash { get; init; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("revokedAt")]
    public DateTimeOffset? RevokedAt { get; init; }
}
