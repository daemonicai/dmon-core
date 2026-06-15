namespace Dmon.Gateway;

/// <summary>
/// Resolved file-system paths for the device-key store.
/// Registered as a singleton and computed once in <c>Program.cs</c>, mirroring
/// <see cref="GatewayProfilePaths"/>.
///
/// Default store directory: <c>~/.dmon/gateway/</c>.
/// Override via <see cref="GatewayOptions.DeviceKeyStoreDirectory"/>.
/// </summary>
internal sealed record GatewayDeviceKeyPaths(
    string DevicesPath,
    string LastSeenPath);
