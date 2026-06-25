namespace Dmon.Network;

/// <summary>
/// The resolved file-system paths used for profile config resolution in the gateway.
/// Registered as a singleton so <see cref="NetworkConnectionEndpoint"/> can perform
/// membership checks against the effective profile set without duplicating the path
/// derivation logic from <c>Program.cs</c>.
/// </summary>
public sealed record NetworkProfilePaths(
    string UserConfigPath,
    string ProjectConfigPath);
