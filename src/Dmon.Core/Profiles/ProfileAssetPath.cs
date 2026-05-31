namespace Dmon.Core.Profiles;

/// <summary>
/// Derives the per-session asset directory path from a workspace root and session id.
/// Consumed by SystemPromptBuilder (Group 4), the permission gate (Group 5),
/// and asset directory provisioning (Group 6).
/// </summary>
internal static class ProfileAssetPath
{
    /// <summary>
    /// Returns the absolute, normalised path of the per-session asset directory:
    /// <c>&lt;workspaceRoot&gt;/assets/&lt;sessionId&gt;/</c>.
    /// </summary>
    /// <param name="workspaceRoot">
    /// The workspace root — typically <see cref="Directory.GetCurrentDirectory()"/>,
    /// the same value the dynamic-context block reports as "Working directory".
    /// </param>
    /// <param name="sessionId">The current session's id.</param>
    internal static string Compute(string workspaceRoot, string sessionId)
        => Path.GetFullPath(Path.Combine(workspaceRoot, "assets", sessionId));
}
