using Dmon.Abstractions.Permissions;

namespace Dmon.Abstractions.Hosting;

/// <summary>
/// DI marker registered by <see cref="DmonRegistrationExtensions.WithPermissionMode{T}"/>.
/// When present, overrides the default <see cref="PermissionMode.Coding"/> posture for the session.
/// When absent, <see cref="PermissionMode.Coding"/> is used.
/// </summary>
public sealed record PermissionModeOptions(PermissionMode Mode);
