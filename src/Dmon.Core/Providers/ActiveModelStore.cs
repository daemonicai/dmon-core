using Dmon.Abstractions.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Providers;

/// <summary>
/// Persists the active provider/model selection via IConfiguration (reads) and by writing
/// the "activeModel" key to .dmon/config.local.yaml in the working directory (saves).
/// No external YAML library — minimal line-based key-preserving rewrite.
/// </summary>
public sealed class ActiveModelStore : IActiveModelStore
{
    private readonly IConfiguration _configuration;
    private readonly string _workingDirectory;

    public ActiveModelStore(IConfiguration configuration, string workingDirectory)
    {
        _configuration = configuration;
        _workingDirectory = workingDirectory;
    }

    /// <inheritdoc/>
    public ModelRef? Load()
    {
        try
        {
            return ModelRef.Parse(_configuration[ConfigurationKeys.ActiveModel]);
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(ModelRef selection, CancellationToken cancellationToken = default)
    {
        string dmonDir = Path.Combine(_workingDirectory, ".dmon");
        Directory.CreateDirectory(dmonDir);

        string filePath = Path.Combine(dmonDir, "config.local.yaml");
        string newLine = $"{ConfigurationKeys.ActiveModel}: {selection}";

        // Read existing lines so other top-level keys are preserved.
        string[] existing = File.Exists(filePath)
            ? await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false)
            : [];

        List<string> output = new(existing.Length + 1);
        bool replaced = false;

        foreach (string line in existing)
        {
            // Match only top-level (non-indented) activeModel: lines; indented occurrences are left untouched.
            if (!replaced && IsActiveModelLine(line))
            {
                output.Add(newLine);
                replaced = true;
            }
            else
            {
                output.Add(line);
            }
        }

        if (!replaced)
        {
            output.Add(newLine);
        }

        string temp = filePath + ".tmp";
        await File.WriteAllLinesAsync(temp, output, cancellationToken).ConfigureAwait(false);
        File.Move(temp, filePath, overwrite: true);
    }

    // Matches a top-level YAML line whose key is ConfigurationKeys.ActiveModel, e.g. "activeModel: ..."
    // The raw (unmodified) line is passed; any leading whitespace means the key is nested and is NOT matched.
    private static bool IsActiveModelLine(string rawLine)
    {
        string key = ConfigurationKeys.ActiveModel;
        if (!rawLine.StartsWith(key, StringComparison.Ordinal))
        {
            return false;
        }
        // Must be immediately followed by ':' to distinguish from e.g. "activeModelOverride:".
        int next = key.Length;
        return next < rawLine.Length && rawLine[next] == ':';
    }
}
