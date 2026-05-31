using Dmon.Abstractions.Profiles;

namespace Dmon.Core.Profiles;

/// <summary>
/// Creates the per-session asset directory under the workspace root
/// when the active profile enables assets.
/// The directory is never deleted by this service — reaping is out of scope for V1.
/// </summary>
internal sealed class SessionAssetProvisioner : ISessionAssetProvisioner
{
    private readonly string _workspaceRoot;

    /// <param name="workspaceRoot">
    /// The workspace root used as the anchor for asset paths.
    /// Callers should pass <see cref="Directory.GetCurrentDirectory()"/> — the same
    /// value used by <see cref="ProfileAssetPath.Compute"/>.
    /// </param>
    internal SessionAssetProvisioner(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
    }

    /// <inheritdoc />
    public string? Provision(AgentProfile profile, string? sessionId)
    {
        if (!profile.Assets || string.IsNullOrEmpty(sessionId))
        {
            return null;
        }

        string path = ProfileAssetPath.Compute(_workspaceRoot, sessionId);
        Directory.CreateDirectory(path);
        return path;
    }
}
