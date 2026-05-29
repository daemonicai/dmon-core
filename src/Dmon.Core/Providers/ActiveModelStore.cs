namespace Dmon.Core.Providers;

/// <summary>
/// Persists the active provider/model selection to .dmon/state.yaml.
/// Scope: project file (<workingDirectory>/.dmon/state.yaml) when a project .dmon
/// directory exists; otherwise global (~/.dmon/state.yaml). Mirrors
/// PermissionSettingsLoader's construction and write pattern — no external YAML
/// library, atomic temp-file + File.Move(overwrite).
/// </summary>
public sealed class ActiveModelStore : IActiveModelStore
{
    private readonly string _filePath;

    private ActiveModelStore(string filePath)
    {
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    /// <summary>
    /// Returns a store scoped to the project at <paramref name="workingDirectory"/>:
    /// uses <c>&lt;workingDirectory&gt;/.dmon/state.yaml</c> when a project .dmon
    /// directory exists there, otherwise falls back to the global path.
    /// </summary>
    public static ActiveModelStore LoadProject(string workingDirectory)
    {
        string projectDmonDir = Path.Combine(workingDirectory, ".dmon");
        if (Directory.Exists(projectDmonDir))
        {
            return new ActiveModelStore(Path.Combine(projectDmonDir, "state.yaml"));
        }

        return LoadGlobal();
    }

    /// <summary>
    /// Returns a store scoped to the global ~/.dmon/state.yaml.
    /// </summary>
    public static ActiveModelStore LoadGlobal()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new ActiveModelStore(Path.Combine(home, ".dmon", "state.yaml"));
    }

    /// <inheritdoc/>
    public ActiveSelection? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            string[] lines = File.ReadAllLines(_filePath);
            return Parse(lines);
        }
        catch
        {
            // Absent or unreadable file must never surface as an exception.
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ActiveSelection selection, CancellationToken cancellationToken = default)
    {
        string dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);

        string temp = _filePath + ".tmp";
        await File.WriteAllTextAsync(temp, Serialise(selection), cancellationToken);
        File.Move(temp, _filePath, overwrite: true);
    }

    // --- Parser ---

    private static ActiveSelection? Parse(IEnumerable<string> lines)
    {
        string? provider = null;
        string? model = null;

        foreach (string rawLine in lines)
        {
            // Strip inline # comments (not in quotes) — same spirit as PermissionSettingsLoader.
            string line = StripComment(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();

            if (key == "activeProvider")
            {
                provider = string.IsNullOrWhiteSpace(value) ? null : value;
            }
            else if (key == "activeModel")
            {
                model = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        // A selection is only meaningful when the provider is present.
        return provider is null ? null : new ActiveSelection(provider, model);
    }

    private static string StripComment(string line)
    {
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote == '\0')
            {
                if (c == '"' || c == '\'') quote = c;
                else if (c == '#') return line[..i];
            }
            else if (c == quote)
            {
                quote = '\0';
            }
        }
        return line;
    }

    // --- Serialiser ---

    private static string Serialise(ActiveSelection selection)
    {
        System.Text.StringBuilder sb = new();
        sb.Append("activeProvider: ").AppendLine(selection.Provider);
        if (selection.Model is not null)
        {
            sb.Append("activeModel: ").AppendLine(selection.Model);
        }
        return sb.ToString();
    }
}
