namespace Dmon.Core.Config;

/// <summary>
/// The computed result of resolving both config scopes: an ordered, deduplicated
/// profile list and the effective <c>defaultProfile</c> value.
/// </summary>
/// <param name="Profiles">
/// Effective profile entries in declaration order (user entries first, then project-only entries),
/// with project-scope winning per-name conflicts.
/// </param>
/// <param name="DefaultProfile">
/// The effective <c>defaultProfile</c> key after applying project-wins precedence,
/// or <see langword="null"/> when neither scope declared one.
/// </param>
public sealed record EffectiveProfileSet(
    IReadOnlyList<ProfileConfigEntry> Profiles,
    string? DefaultProfile);

/// <summary>
/// Computes the effective set of agent profiles by unioning the user and project
/// <c>profiles:</c> maps, deduplicating by name (case-insensitive), and applying
/// project-wins precedence.
/// </summary>
/// <remarks>
/// Load order: user entries first (in file order), then project entries (in file order).
/// Where the same name appears in both scopes the user-file position is kept but the
/// project entry's fields win. For <c>defaultProfile</c>, the project value wins when both
/// scopes declare one; if only one scope declares it, that value is used.
/// </remarks>
public sealed class EffectiveProfileSetResolver
{
    private readonly ProfilesConfigReader _reader;

    public EffectiveProfileSetResolver() : this(new ProfilesConfigReader()) { }

    public EffectiveProfileSetResolver(ProfilesConfigReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Reads both config files and returns the effective profile set.
    /// A missing file or absent <c>profiles:</c> section contributes an empty list.
    /// </summary>
    public EffectiveProfileSet Resolve(string userConfigPath, string projectConfigPath)
    {
        (IReadOnlyList<ProfileConfigEntry> userEntries, string? userDefault) = _reader.Read(userConfigPath);
        (IReadOnlyList<ProfileConfigEntry> projectEntries, string? projectDefault) = _reader.Read(projectConfigPath);

        IReadOnlyList<ProfileConfigEntry> profiles = ComputeEffectiveSet(userEntries, projectEntries);

        // Project wins for defaultProfile; fall back to user value when project is absent.
        string? effectiveDefault = projectDefault ?? userDefault;

        return new EffectiveProfileSet(profiles, effectiveDefault);
    }

    /// <summary>
    /// Pure function — no file I/O. Computes the ordered, deduplicated effective profile set
    /// from two already-read entry lists.
    /// </summary>
    /// <param name="userEntries">Entries from the user-scoped config file, in file order.</param>
    /// <param name="projectEntries">Entries from the project-scoped config file, in file order.</param>
    /// <returns>
    /// User entries followed by project-only entries, each in their original file order,
    /// with no name appearing more than once (case-insensitive). Shared names use the
    /// project entry's fields.
    /// </returns>
    public static IReadOnlyList<ProfileConfigEntry> ComputeEffectiveSet(
        IReadOnlyList<ProfileConfigEntry> userEntries,
        IReadOnlyList<ProfileConfigEntry> projectEntries)
    {
        // Index project entries by normalised name; first occurrence within project wins.
        Dictionary<string, ProfileConfigEntry> projectByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileConfigEntry entry in projectEntries)
        {
            projectByName.TryAdd(entry.Name, entry);
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<ProfileConfigEntry> result = [];

        // Walk user entries then project entries in file order.
        foreach (ProfileConfigEntry entry in userEntries.Concat(projectEntries))
        {
            if (!seen.Add(entry.Name))
            {
                continue;
            }

            // Project entry fields win for shared names; position (user-order) is preserved.
            ProfileConfigEntry effective = projectByName.TryGetValue(entry.Name, out ProfileConfigEntry? projectEntry)
                ? projectEntry
                : entry;

            result.Add(effective);
        }

        return result;
    }
}
