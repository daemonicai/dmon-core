namespace Dmon.Network;

/// <summary>
/// Resolved file-system paths for the device-key store.
/// Registered as a singleton and computed once in <c>Program.cs</c>, mirroring
/// <see cref="NetworkProfilePaths"/>.
///
/// Default store directory: <c>~/.dmon/gateway/</c>.
/// Override via <see cref="NetworkOptions.DeviceKeyStoreDirectory"/>.
/// </summary>
internal sealed record NetworkDeviceKeyPaths(
    string DevicesPath,
    string LastSeenPath);
