namespace Dmon.Core.Permissions;

/// <summary>
/// Loads and saves permission settings from .dmon/settings.yaml (project) or
/// ~/.dmon/settings.yaml (global). Uses a minimal line-based YAML parser that
/// handles only the permissions: block shape from ADR-006 — no external YAML library.
/// </summary>
public sealed class PermissionSettingsLoader : IPermissionSettings
{
    private readonly string _filePath;
    private PermissionSettings _settings;

    private PermissionSettingsLoader(string filePath, PermissionSettings settings)
    {
        _filePath = filePath;
        _settings = settings;
    }

    public PermissionSettings Settings => _settings;

    public static PermissionSettingsLoader LoadProject(string workingDirectory)
    {
        string path = Path.Combine(workingDirectory, ".dmon", "settings.yaml");
        return new PermissionSettingsLoader(path, Load(path));
    }

    public static PermissionSettingsLoader LoadGlobal()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path = Path.Combine(home, ".dmon", "settings.yaml");
        return new PermissionSettingsLoader(path, Load(path));
    }

    public async Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
    {
        string dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        string temp = _filePath + ".tmp";
        await File.WriteAllTextAsync(temp, Serialise(updated), cancellationToken);
        File.Move(temp, _filePath, overwrite: true);

        _settings = updated;
    }

    // --- Parser ---

    private static PermissionSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PermissionSettings();
        }

        string[] lines = File.ReadAllLines(path);
        return Parse(lines);
    }

    public static PermissionSettings Parse(IEnumerable<string> lines)
    {
        string? currentTier = null;
        string? currentList = null;

        List<string> readAllow = [];
        List<string> readDeny = [];
        List<string> writeAllow = [];
        List<string> writeDeny = [];
        List<string> bashAllow = [];
        List<string> bashDeny = [];
        List<string> httpAllow = [];
        List<string> httpDeny = [];

        bool inPermissions = false;

        foreach (string rawLine in lines)
        {
            string line = rawLine;

            // Strip inline comments, honouring single- and double-quoted regions
            // so that '#' inside a quoted list-item value is preserved.
            int commentIdx = FindCommentIndex(line);
            if (commentIdx >= 0)
            {
                line = line[..commentIdx];
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int indent = CountLeadingSpaces(line);
            string trimmed = line.TrimStart();

            if (indent == 0)
            {
                inPermissions = trimmed.TrimEnd(':', ' ') == "permissions";
                currentTier = null;
                currentList = null;
                continue;
            }

            if (!inPermissions)
            {
                continue;
            }

            if (indent == 2)
            {
                // Tier key: read, write, bash, http
                currentTier = trimmed.TrimEnd(':', ' ');
                currentList = null;
                continue;
            }

            if (indent == 4)
            {
                // List key: allow, deny
                currentList = trimmed.TrimEnd(':', ' ');
                continue;
            }

            if (indent >= 6 && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                // List item
                string value = StripQuotes(trimmed[2..].Trim());
                AddItem(currentTier, currentList, value,
                    readAllow, readDeny, writeAllow, writeDeny,
                    bashAllow, bashDeny, httpAllow, httpDeny);
            }
        }

        return new PermissionSettings
        {
            Read = new TierSettings { Allow = readAllow, Deny = readDeny },
            Write = new TierSettings { Allow = writeAllow, Deny = writeDeny },
            Bash = new TierSettings { Allow = bashAllow, Deny = bashDeny },
            Http = new TierSettings { Allow = httpAllow, Deny = httpDeny }
        };
    }

    private static int FindCommentIndex(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote == '\0')
            {
                if (c == '"' || c == '\'')
                {
                    quote = c;
                }
                else if (c == '#')
                {
                    return i;
                }
            }
            else if (c == quote)
            {
                quote = '\0';
            }
        }
        return -1;
    }

    private static void AddItem(
        string? tier, string? list, string value,
        List<string> readAllow, List<string> readDeny,
        List<string> writeAllow, List<string> writeDeny,
        List<string> bashAllow, List<string> bashDeny,
        List<string> httpAllow, List<string> httpDeny)
    {
        if (tier is null || list is null)
        {
            return;
        }

        List<string>? target = (tier, list) switch
        {
            ("read", "allow") => readAllow,
            ("read", "deny") => readDeny,
            ("write", "allow") => writeAllow,
            ("write", "deny") => writeDeny,
            ("bash", "allow") => bashAllow,
            ("bash", "deny") => bashDeny,
            ("http", "allow") => httpAllow,
            ("http", "deny") => httpDeny,
            _ => null
        };

        target?.Add(value);
    }

    private static int CountLeadingSpaces(string line)
    {
        int count = 0;
        foreach (char c in line)
        {
            if (c == ' ') count++;
            else break;
        }
        return count;
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static string QuoteIfNeeded(string value)
    {
        if (value.Contains('#') || value.Contains(':') || value != value.Trim())
            return $"\"{value}\"";
        return value;
    }

    // --- Serialiser ---

    public static string Serialise(PermissionSettings settings)
    {
        System.Text.StringBuilder sb = new();
        sb.AppendLine("permissions:");
        AppendTier(sb, "read", settings.Read);
        AppendTier(sb, "write", settings.Write);
        AppendTier(sb, "bash", settings.Bash);
        AppendTier(sb, "http", settings.Http);
        return sb.ToString();
    }

    private static void AppendTier(System.Text.StringBuilder sb, string name, TierSettings tier)
    {
        if (tier.Allow.Count == 0 && tier.Deny.Count == 0)
        {
            return;
        }

        sb.Append("  ").Append(name).AppendLine(":");
        AppendList(sb, "allow", tier.Allow);
        AppendList(sb, "deny", tier.Deny);
    }

    private static void AppendList(System.Text.StringBuilder sb, string name, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        sb.Append("    ").Append(name).AppendLine(":");
        foreach (string item in items)
        {
            sb.Append("      - ").AppendLine(QuoteIfNeeded(item));
        }
    }
}
