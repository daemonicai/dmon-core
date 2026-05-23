namespace Dmon.BuiltinTools.Bash;

public sealed class DenylistChecker : IDenylistChecker
{
    // System directories that must not be targeted by destructive commands.
    private static readonly string[] SystemPaths =
    [
        "/",
        "/etc",
        "/usr",
        "/bin",
        "/sbin",
        "/boot",
        "/dev",
        "/proc",
        "/sys",
        "/lib",
        "/lib64",
        "/var"
    ];

    public bool IsDenied(string command)
    {
        string trimmed = command.TrimStart();

        return IsRmSystemPath(trimmed)
            || IsMkfs(trimmed)
            || IsDdZero(trimmed)
            || IsShred(trimmed)
            || IsChmodSystemPath(trimmed)
            || IsChattr(trimmed)
            || IsForkBomb(trimmed)
            || IsSudo(trimmed)
            || IsSu(trimmed);
    }

    private static bool IsRmSystemPath(string trimmed)
    {
        // Must start with rm (with optional flags before paths).
        if (!trimmed.StartsWith("rm ", StringComparison.Ordinal) && trimmed != "rm")
        {
            return false;
        }

        return ContainsSystemPathArg(trimmed, startIndex: 2);
    }

    private static bool IsMkfs(string trimmed)
    {
        return trimmed == "mkfs"
            || trimmed.StartsWith("mkfs ", StringComparison.Ordinal)
            || trimmed.StartsWith("mkfs.", StringComparison.Ordinal);
    }

    private static bool IsDdZero(string trimmed)
    {
        return trimmed.StartsWith("dd ", StringComparison.Ordinal)
            && trimmed.Contains("if=/dev/zero", StringComparison.Ordinal);
    }

    private static bool IsShred(string trimmed)
    {
        return trimmed.StartsWith("shred ", StringComparison.Ordinal) || trimmed == "shred";
    }

    private static bool IsChmodSystemPath(string trimmed)
    {
        if (!trimmed.StartsWith("chmod ", StringComparison.Ordinal))
        {
            return false;
        }

        // Match chmod [opts] [mode] [path] where path is a system path.
        // We look for 777 or -R 777 patterns targeting system paths.
        if (!trimmed.Contains("777", StringComparison.Ordinal))
        {
            return false;
        }

        return ContainsSystemPathArg(trimmed, startIndex: 5);
    }

    private static bool IsChattr(string trimmed)
    {
        if (!trimmed.StartsWith("chattr ", StringComparison.Ordinal))
        {
            return false;
        }

        return ContainsSystemPathArg(trimmed, startIndex: 6, includeSubpaths: true);
    }

    private static bool IsForkBomb(string trimmed)
    {
        // Fork bomb pattern: :(){ :|:& };: — look for :|: or :()
        return trimmed.Contains(":|:", StringComparison.Ordinal)
            || trimmed.Contains(":()", StringComparison.Ordinal);
    }

    private static bool IsSudo(string trimmed)
    {
        return trimmed.StartsWith("sudo ", StringComparison.Ordinal) || trimmed == "sudo";
    }

    private static bool IsSu(string trimmed)
    {
        // Match `su` or `su <args>` but NOT `sudo`, `subversion`, `sublime`, etc.
        return trimmed == "su" || trimmed.StartsWith("su ", StringComparison.Ordinal);
    }

    // Returns true if the substring of the command after startIndex contains a system path
    // as a standalone argument (i.e. the path is exactly one of the system paths, not as
    // a prefix of a longer path like /var/myapp).
    // When includeSubpaths is true, tokens that are files/dirs under a system path also match
    // (used for chattr/chmod targeting /etc/passwd). When false, only the exact system path
    // root matches (used for rm, where /var/myapp should NOT be denied).
    private static bool ContainsSystemPathArg(string command, int startIndex, bool includeSubpaths = false)
    {
        string args = startIndex < command.Length ? command[startIndex..] : string.Empty;

        string[] tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (string token in tokens)
        {
            // Skip flags and chattr-style attribute specs (+i, -i).
            if (token.StartsWith('-') || token.StartsWith('+'))
            {
                continue;
            }

            // Normalise trailing slashes, but preserve bare "/" as itself.
            string normalised = token == "/" ? "/" : token.TrimEnd('/');

            foreach (string sysPath in SystemPaths)
            {
                if (string.Equals(normalised, sysPath, StringComparison.Ordinal))
                {
                    return true;
                }

                if (includeSubpaths && sysPath != "/" &&
                    normalised.StartsWith(sysPath + "/", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
