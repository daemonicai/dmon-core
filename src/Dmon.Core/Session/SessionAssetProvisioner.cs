
namespace Dmon.Core.Session;

/// <summary>
/// Creates the per-session asset directory under the workspace root
/// when asset support is enabled via <see cref="Dmon.Abstractions.Hosting.AssetsOptions"/>.
/// The directory is never deleted by this service — reaping is out of scope for V1.
/// </summary>
internal sealed class SessionAssetProvisioner : ISessionAssetProvisioner
{
    /// <inheritdoc />
    public string? Provision(bool assetsEnabled, string? workspaceRoot, string? sessionId)
    {
        if (!assetsEnabled || string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        string root = workspaceRoot ?? Directory.GetCurrentDirectory();
        string path = SessionAssetPath.Compute(root, sessionId);
        Directory.CreateDirectory(path);
        return path;
    }
}
