namespace Dmon.Core.Permissions;

/// <summary>
/// Evaluates permission requests against project and global settings per ADR-006.
///
/// Grant precedence:
///   - Path checks: longest matching prefix wins. Ties resolve to Deny.
///   - Bash: deny beats allow regardless of pattern specificity.
///   - Project settings beat global settings.
/// </summary>
public sealed class PermissionPolicy : IPermissionPolicy
{
    private readonly IPermissionSettings _project;
    private readonly IPermissionSettings? _global;
    private readonly string _workingDirectory;
    private readonly IBashCompositeDetector _compositeDetector;
    private readonly IDenylistChecker _denylist;

    public PermissionPolicy(
        IPermissionSettings project,
        IPermissionSettings? global,
        string workingDirectory,
        IBashCompositeDetector compositeDetector,
        IDenylistChecker denylist)
    {
        _project = project;
        _global = global;
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _compositeDetector = compositeDetector;
        _denylist = denylist;
    }

    public PermissionResult EvaluateRead(string path)
    {
        string normalised = NormalisePath(path);

        // CWD subtree is implicitly allowed.
        if (IsUnder(normalised, _workingDirectory))
        {
            return PermissionResult.Allow;
        }

        // Check project allow, then global allow. Longest-prefix wins per scope;
        // project scope beats global.
        int projectScore = LongestPrefixMatch(normalised, _project.Settings.Read.Allow);
        if (projectScore >= 0)
        {
            return PermissionResult.Allow;
        }

        if (_global is not null)
        {
            int globalScore = LongestPrefixMatch(normalised, _global.Settings.Read.Allow);
            if (globalScore >= 0)
            {
                return PermissionResult.Allow;
            }
        }

        return PermissionResult.Prompt;
    }

    public PermissionResult EvaluateWrite(string path)
    {
        string normalised = NormalisePath(path);

        // Check deny first (longest prefix wins over allow; project beats global).
        PermissionResult? explicitResult = ResolvePathGrant(
            normalised,
            _project.Settings.Write,
            _global?.Settings.Write);

        return explicitResult ?? PermissionResult.Prompt;
    }

    public PermissionResult EvaluateBash(string command)
    {
        // Composites always prompt — cannot be stored as persistent approvals.
        if (_compositeDetector.IsComposite(command))
        {
            return PermissionResult.Prompt;
        }

        // Hardcoded denylist — cannot be overridden by any allow rule.
        if (_denylist.IsDenied(command))
        {
            return PermissionResult.Deny;
        }

        // For bash: deny beats allow. Check deny lists first.
        if (MatchesBashGlob(command, _project.Settings.Bash.Deny))
        {
            return PermissionResult.Deny;
        }

        if (_global is not null && MatchesBashGlob(command, _global.Settings.Bash.Deny))
        {
            return PermissionResult.Deny;
        }

        // Check allow lists — project then global.
        if (MatchesBashGlob(command, _project.Settings.Bash.Allow))
        {
            return PermissionResult.Allow;
        }

        if (_global is not null && MatchesBashGlob(command, _global.Settings.Bash.Allow))
        {
            return PermissionResult.Allow;
        }

        return PermissionResult.Prompt;
    }

    public PermissionResult EvaluateHttp(string domain)
    {
        // HTTP: project-level only (ADR-006). Exact subdomain match.
        foreach (string allowed in _project.Settings.Http.Allow)
        {
            if (string.Equals(domain, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return PermissionResult.Allow;
            }
        }

        return PermissionResult.Prompt;
    }

    // --- Helpers ---

    private static string NormalisePath(string path)
    {
        // Syntactic normalisation only — resolves . and .. without filesystem access.
        return Path.GetFullPath(path);
    }

    private static bool IsUnder(string path, string directory)
    {
        // Ensure directory ends with separator for prefix matching.
        string dir = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        return path.StartsWith(dir, StringComparison.Ordinal)
            || string.Equals(path, directory, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the length of the longest matching prefix from <paramref name="patterns"/>,
    /// or -1 if no pattern matches.
    /// </summary>
    private static int LongestPrefixMatch(string path, IReadOnlyList<string> patterns)
    {
        int best = -1;

        foreach (string pattern in patterns)
        {
            string normalised = NormalisePath(pattern);

            if (IsUnder(path, normalised) || string.Equals(path, normalised, StringComparison.Ordinal))
            {
                if (normalised.Length > best)
                {
                    best = normalised.Length;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Resolves write/path grant with longest-prefix precedence and project-beats-global.
    /// Returns null when neither scope has a match (caller falls through to Prompt).
    /// </summary>
    private static PermissionResult? ResolvePathGrant(
        string path,
        TierSettings projectTier,
        TierSettings? globalTier)
    {
        int projectAllowScore = LongestPrefixMatch(path, projectTier.Allow);
        int projectDenyScore = LongestPrefixMatch(path, projectTier.Deny);

        if (projectAllowScore >= 0 || projectDenyScore >= 0)
        {
            // Longest wins; ties → Deny.
            if (projectAllowScore > projectDenyScore)
            {
                return PermissionResult.Allow;
            }

            return PermissionResult.Deny;
        }

        if (globalTier is null)
        {
            return null;
        }

        int globalAllowScore = LongestPrefixMatch(path, globalTier.Allow);
        int globalDenyScore = LongestPrefixMatch(path, globalTier.Deny);

        if (globalAllowScore >= 0 || globalDenyScore >= 0)
        {
            if (globalAllowScore > globalDenyScore)
            {
                return PermissionResult.Allow;
            }

            return PermissionResult.Deny;
        }

        return null;
    }

    private static bool MatchesBashGlob(string command, IReadOnlyList<string> patterns)
    {
        foreach (string pattern in patterns)
        {
            if (GlobMatch(command, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Simple glob match supporting only <c>*</c> as a wildcard (matches any sequence of
    /// characters including empty). No other glob metacharacters are interpreted.
    /// </summary>
    private static bool GlobMatch(string input, string pattern)
    {
        // Translate pattern to a simple recursive match.
        return GlobMatchSpan(input.AsSpan(), pattern.AsSpan());
    }

    private static bool GlobMatchSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
    {
        while (!pattern.IsEmpty)
        {
            if (pattern[0] == '*')
            {
                // Consume all leading stars.
                pattern = pattern[1..];

                if (pattern.IsEmpty)
                {
                    return true; // trailing * matches everything
                }

                // Try matching the rest of the pattern at each position.
                for (int i = 0; i <= input.Length; i++)
                {
                    if (GlobMatchSpan(input[i..], pattern))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (input.IsEmpty || input[0] != pattern[0])
            {
                return false;
            }

            input = input[1..];
            pattern = pattern[1..];
        }

        return input.IsEmpty;
    }
}
