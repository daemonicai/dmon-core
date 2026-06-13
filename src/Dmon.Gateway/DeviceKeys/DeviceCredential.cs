namespace Dmon.Gateway.DeviceKeys;

/// <summary>
/// A single device credential row from <c>devices.json</c>.
/// An entry is active when <see cref="RevokedAt"/> is <see langword="null"/>.
/// </summary>
internal sealed record DeviceCredential(
    string KeyId,
    string Name,
    string SecretHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt);
