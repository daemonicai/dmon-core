using Dmon.Abstractions.Profiles;

namespace Dmon.Core.Profiles;

/// <summary>
/// Provisions the per-session asset directory when the active profile enables assets.
/// </summary>
public interface ISessionAssetProvisioner
{
    /// <summary>
    /// Creates <c>assets/&lt;sessionId&gt;/</c> under the workspace root when
    /// <paramref name="profile"/>.Assets is <see langword="true"/> and
    /// <paramref name="sessionId"/> is non-null.
    /// Returns the provisioned path, or <see langword="null"/> when no directory was created.
    /// </summary>
    string? Provision(AgentProfile profile, string? sessionId);
}
