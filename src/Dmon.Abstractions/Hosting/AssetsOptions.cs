namespace Dmon.Abstractions.Hosting;

/// <summary>
/// DI marker registered by <see cref="DmonRegistrationExtensions.UseAssets{T}"/>.
/// When present, the session-asset provisioner creates <c>assets/&lt;sessionId&gt;/</c>
/// under the configured path (or the workspace root when <see cref="Path"/> is null).
/// When absent, no asset directory is created and no asset-directory line appears in
/// the system prompt's dynamic context.
/// </summary>
/// <param name="Path">
/// Workspace root under which <c>assets/&lt;sessionId&gt;/</c> is created.
/// <see langword="null"/> uses <see cref="System.IO.Directory.GetCurrentDirectory()"/> at
/// provisioning time.
/// </param>
public sealed record AssetsOptions(string? Path);
