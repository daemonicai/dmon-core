namespace Dmon.Core.Config;

/// <summary>
/// Resolves agent config files from well-known locations and combines their
/// content into a single <see cref="AgentConfigResult"/>.
/// </summary>
/// <remarks>
/// Resolution order:
/// <list type="number">
///   <item><c>~/.dmon/AGENTS.md</c> — user-level, always read silently if present.</item>
///   <item><c>{CWD}/AGENTS.md</c> — project-level, always read silently if present.</item>
///   <item><c>{CWD}/CLAUDE.md</c> — only if <c>{CWD}/AGENTS.md</c> is absent;
///         sets <see cref="AgentConfigResult.ClaudeMdUsed"/> so the caller can
///         emit a <c>system.notice</c> event.</item>
/// </list>
/// When both user-level and project-level configs are present their content is
/// combined with user config first, separated by a Markdown section header.
/// </remarks>
public sealed class AgentConfigResolver
{
    private const string DmonDir = ".dmon";
    private const string AgentsMd = "AGENTS.md";
    private const string ClaudeMd = "CLAUDE.md";
    private const string SectionSeparator = "\n\n## Project configuration\n\n";

    public async Task<AgentConfigResult> ResolveAsync(CancellationToken cancellationToken)
    {
        string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string cwd = Directory.GetCurrentDirectory();

        string userAgentsPath = Path.Combine(userHome, DmonDir, AgentsMd);
        string projectAgentsPath = Path.Combine(cwd, AgentsMd);
        string claudeMdPath = Path.Combine(cwd, ClaudeMd);

        string? userText = await TryReadAsync(userAgentsPath, cancellationToken).ConfigureAwait(false);

        bool projectAgentsExists = File.Exists(projectAgentsPath);
        bool claudeMdUsed = false;
        string? projectText;

        if (projectAgentsExists)
        {
            projectText = await TryReadAsync(projectAgentsPath, cancellationToken).ConfigureAwait(false);
        }
        else if (File.Exists(claudeMdPath))
        {
            projectText = await TryReadAsync(claudeMdPath, cancellationToken).ConfigureAwait(false);
            claudeMdUsed = projectText is not null;
        }
        else
        {
            projectText = null;
        }

        string? combined = Combine(userText, projectText);

        if (combined is null && !claudeMdUsed)
        {
            return AgentConfigResult.Empty;
        }

        return new AgentConfigResult
        {
            Text = combined,
            ClaudeMdUsed = claudeMdUsed
        };
    }

    private static string? Combine(string? userText, string? projectText)
    {
        if (userText is null && projectText is null)
        {
            return null;
        }

        if (userText is null)
        {
            return projectText;
        }

        if (projectText is null)
        {
            return userText;
        }

        return userText + SectionSeparator + projectText;
    }

    private static async Task<string?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(content) ? null : content;
    }
}
