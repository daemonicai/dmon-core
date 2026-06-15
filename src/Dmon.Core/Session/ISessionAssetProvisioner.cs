namespace Dmon.Core.Session;

/// <summary>
/// Provisions the per-session asset directory when asset support is enabled via
/// <see cref="Dmon.Abstractions.Hosting.AssetsOptions"/>.
/// </summary>
public interface ISessionAssetProvisioner
{
    /// <summary>
    /// Creates <c>assets/&lt;sessionId&gt;/</c> under the workspace root when
    /// <paramref name="assetsEnabled"/> is <see langword="true"/> and
    /// <paramref name="sessionId"/> is non-null.
    /// Returns the provisioned path, or <see langword="null"/> when no directory was created.
    /// </summary>
    /// <param name="assetsEnabled">Whether asset directory provisioning is active.</param>
    /// <param name="workspaceRoot">
    /// Root under which <c>assets/&lt;sessionId&gt;/</c> is created.
    /// <see langword="null"/> falls back to <see cref="Directory.GetCurrentDirectory()"/>.
    /// </param>
    /// <param name="sessionId">The current session's id.</param>
    string? Provision(bool assetsEnabled, string? workspaceRoot, string? sessionId);
}
