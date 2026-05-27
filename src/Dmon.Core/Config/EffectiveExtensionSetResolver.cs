namespace Dmon.Core.Config;

/// <summary>
/// Computes the effective set of extensions by unioning the user and project
/// <c>extensions</c> lists, deduplicating by normalized source, and applying
/// project-wins precedence for per-entry settings.
/// </summary>
/// <remarks>
/// Load order: user entries first (in file order), then project entries (in file order).
/// Where the same normalized source appears in both scopes, the entry keeps its user-file
/// position and uses the project entry's settings.
/// </remarks>
public sealed class EffectiveExtensionSetResolver
{
    private readonly ExtensionsConfigReader _reader;

    public EffectiveExtensionSetResolver() : this(new ExtensionsConfigReader()) { }

    public EffectiveExtensionSetResolver(ExtensionsConfigReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Reads both config files and returns the ordered, deduplicated effective list.
    /// A missing or absent-<c>extensions</c>-key file contributes an empty list.
    /// </summary>
    public IReadOnlyList<ExtensionEntry> Resolve(string userConfigPath, string projectConfigPath)
    {
        IReadOnlyList<ExtensionEntry> userEntries = _reader.Read(userConfigPath);
        IReadOnlyList<ExtensionEntry> projectEntries = _reader.Read(projectConfigPath);
        return ComputeEffectiveSet(userEntries, projectEntries);
    }

    /// <summary>
    /// Pure function — no file I/O. Computes the ordered, deduplicated effective set from
    /// two already-read entry lists.
    /// </summary>
    /// <param name="userEntries">Entries from the user-scoped config file, in file order.</param>
    /// <param name="projectEntries">Entries from the project-scoped config file, in file order.</param>
    /// <returns>
    /// User entries followed by project-only entries, each in their original file order,
    /// with no source appearing more than once. Shared sources use the project entry's settings.
    /// </returns>
    public static IReadOnlyList<ExtensionEntry> ComputeEffectiveSet(
        IReadOnlyList<ExtensionEntry> userEntries,
        IReadOnlyList<ExtensionEntry> projectEntries)
    {
        // Index project entries by normalised key; first occurrence wins within project.
        Dictionary<string, ExtensionEntry> projectByKey = new(StringComparer.Ordinal);
        foreach (ExtensionEntry entry in projectEntries)
        {
            string key = ExtensionSourceNormalizer.Normalize(entry.Source);
            projectByKey.TryAdd(key, entry);
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        List<ExtensionEntry> result = [];

        // Walk user entries then project entries in file order.
        foreach (ExtensionEntry entry in userEntries.Concat(projectEntries))
        {
            string key = ExtensionSourceNormalizer.Normalize(entry.Source);
            if (!seen.Add(key))
            {
                continue;
            }

            // Settings from the project entry win; the source string is kept from
            // the first-seen occurrence (user entry for shared sources).
            // NOTE: the "version" key in Settings is reserved for future use and
            // does not participate in the dedup key — only inline source version does.
            IReadOnlyDictionary<string, string?> settings = projectByKey.TryGetValue(key, out ExtensionEntry? projectEntry)
                ? projectEntry.Settings
                : entry.Settings;

            result.Add(entry with { Settings = settings });
        }

        return result;
    }


}
