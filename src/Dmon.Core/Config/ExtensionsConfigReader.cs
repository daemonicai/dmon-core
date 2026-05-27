using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Config;

/// <summary>
/// Reads the <c>extensions</c> list from a single <c>config.yaml</c> file into
/// typed <see cref="ExtensionEntry"/> values.
/// </summary>
/// <remarks>
/// Intentionally builds its own <see cref="IConfigurationRoot"/> from the given
/// path rather than reading the shared, layered <c>IConfiguration</c>: the
/// layered provider replaces array elements by index and would silently drop
/// entries when user and project configs are both present.
/// </remarks>
public sealed class ExtensionsConfigReader
{
    public IReadOnlyList<ExtensionEntry> Read(string configFilePath)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddYamlFile(configFilePath, optional: true, reloadOnChange: false)
            .Build();

        IConfigurationSection extensionsSection = config.GetSection("extensions");

        if (!extensionsSection.Exists())
        {
            return [];
        }

        List<ExtensionEntry> result = [];

        foreach (IConfigurationSection entry in extensionsSection.GetChildren())
        {
            string? source = entry["source"];

            // Skip malformed entries rather than aborting the whole file: one
            // bad line must not prevent valid extensions from loading.
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            Dictionary<string, string?> settings = new(StringComparer.OrdinalIgnoreCase);

            foreach (IConfigurationSection child in entry.GetChildren())
            {
                if (!string.Equals(child.Key, "source", StringComparison.OrdinalIgnoreCase))
                {
                    settings[child.Key] = child.Value;
                }
            }

            result.Add(new ExtensionEntry(source, settings));
        }

        return result;
    }
}
