using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;

namespace Dmon.Core.Permissions;

public sealed class PermissionPolicy : IPermissionPolicy
{
    private readonly IPermissionSettings _project;
    private readonly IPermissionSettings? _global;

    public PermissionPolicy(IPermissionSettings project, IPermissionSettings? global)
    {
        _project = project;
        _global = global;
    }

    public IPermissionSettings ProjectSettings => _project;
    public IPermissionSettings? GlobalSettings => _global;

    // --- Static helpers retained for use by built-in tools ---

    internal static string NormalisePath(string path) => Path.GetFullPath(path);

    internal static bool IsUnder(string path, string directory)
    {
        string dir = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(dir, StringComparison.Ordinal)
            || string.Equals(path, directory, StringComparison.Ordinal);
    }

    internal static int LongestPrefixMatch(string path, IReadOnlyList<string> patterns)
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

    internal static PermissionResult? ResolvePathGrant(
        string path,
        TierSettings projectTier,
        TierSettings? globalTier)
    {
        int projectAllowScore = LongestPrefixMatch(path, projectTier.Allow);
        int projectDenyScore = LongestPrefixMatch(path, projectTier.Deny);

        if (projectAllowScore >= 0 || projectDenyScore >= 0)
        {
            return projectAllowScore > projectDenyScore
                ? PermissionResult.Allow
                : PermissionResult.Deny;
        }

        if (globalTier is null)
        {
            return null;
        }

        int globalAllowScore = LongestPrefixMatch(path, globalTier.Allow);
        int globalDenyScore = LongestPrefixMatch(path, globalTier.Deny);

        if (globalAllowScore >= 0 || globalDenyScore >= 0)
        {
            return globalAllowScore > globalDenyScore
                ? PermissionResult.Allow
                : PermissionResult.Deny;
        }

        return null;
    }

    internal static bool MatchesBashGlob(string command, IReadOnlyList<string> patterns)
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

    internal static bool GlobMatch(string input, string pattern)
        => GlobMatchSpan(input.AsSpan(), pattern.AsSpan());

    private static bool GlobMatchSpan(ReadOnlySpan<char> input, ReadOnlySpan<char> pattern)
    {
        while (!pattern.IsEmpty)
        {
            if (pattern[0] == '*')
            {
                pattern = pattern[1..];
                if (pattern.IsEmpty) return true;
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
