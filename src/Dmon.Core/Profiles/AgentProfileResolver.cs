using Dmon.Abstractions.Profiles;
using Dmon.Core.Config;

namespace Dmon.Core.Profiles;

/// <summary>
/// Resolves an <see cref="AgentProfile"/> for an incoming session by merging the
/// effective config set with the built-in <c>coding</c> profile, applying selection
/// precedence, and reading persona text from inline or file sources.
/// </summary>
/// <remarks>
/// Selection precedence (highest to lowest):
/// <list type="number">
///   <item><description>Per-session <c>requestedProfile</c> (non-null)</description></item>
///   <item><description>Effective <c>defaultProfile</c> from config</description></item>
///   <item><description>Built-in <c>coding</c></description></item>
/// </list>
/// A non-null <c>requestedProfile</c> or <c>defaultProfile</c> that names a profile
/// absent from the effective set is a hard, actionable error — no session is created.
/// </remarks>
public sealed class AgentProfileResolver : IAgentProfileResolver
{
    private readonly EffectiveProfileSetResolver _setResolver;
    private readonly string _userConfigPath;
    private readonly string _projectConfigPath;

    public AgentProfileResolver(
        EffectiveProfileSetResolver setResolver,
        string userConfigPath,
        string projectConfigPath)
    {
        _setResolver = setResolver;
        _userConfigPath = userConfigPath;
        _projectConfigPath = projectConfigPath;
    }

    public Task<AgentProfile> ResolveAsync(
        string? requestedProfile,
        CancellationToken cancellationToken)
    {
        EffectiveProfileSet effectiveSet = _setResolver.Resolve(_userConfigPath, _projectConfigPath);

        // Merge: config profiles ∪ built-in coding, deduped case-insensitively.
        // A config entry named "coding" overrides the built-in (config is the user's explicit choice).
        Dictionary<string, ProfileConfigEntry> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileConfigEntry entry in effectiveSet.Profiles)
        {
            byName.TryAdd(entry.Name, entry);
        }

        bool builtInCodingOverridden = byName.ContainsKey(BuiltInProfiles.CodingProfileName);

        // Apply selection precedence.
        string selectedName;

        if (requestedProfile is not null)
        {
            selectedName = requestedProfile;
        }
        else if (effectiveSet.DefaultProfile is not null)
        {
            selectedName = effectiveSet.DefaultProfile;
        }
        else
        {
            // No selection → built-in coding.
            return Task.FromResult(BuiltInProfiles.Coding);
        }

        // A named selection must exist in the effective set (config union built-in).
        bool isBuiltInCoding = string.Equals(selectedName, BuiltInProfiles.CodingProfileName, StringComparison.OrdinalIgnoreCase);

        if (isBuiltInCoding && !builtInCodingOverridden)
        {
            // Selection names the built-in coding profile explicitly and it hasn't been overridden.
            return Task.FromResult(BuiltInProfiles.Coding);
        }

        if (!byName.TryGetValue(selectedName, out ProfileConfigEntry? chosen))
        {
            string source = requestedProfile is not null ? "per-session profile" : "defaultProfile config";
            throw new AgentProfileConfigException(
                $"Unknown profile '{selectedName}' specified as {source}. " +
                $"Available profiles: {BuildAvailableList(byName, builtInCodingOverridden)}.");
        }

        AgentProfile resolved = BuildProfile(chosen);
        return Task.FromResult(resolved);
    }

    private static AgentProfile BuildProfile(ProfileConfigEntry entry)
    {
        ValidateCoherence(entry);

        string persona = ReadPersona(entry);

        return new AgentProfile(
            Name: entry.Name,
            Persona: persona,
            Assets: entry.Assets,
            PermissionMode: entry.PermissionMode);
    }

    private static void ValidateCoherence(ProfileConfigEntry entry)
    {
        // Task 3.4: sandbox + assets:false is incoherent.
        if (entry.PermissionMode == PermissionMode.Sandbox && !entry.Assets)
        {
            throw new AgentProfileConfigException(
                $"Profile '{entry.Name}' has permissionMode 'sandbox' but assets is false. " +
                $"Sandbox profiles require an asset directory (set assets: true).");
        }
    }

    private static string ReadPersona(ProfileConfigEntry entry)
    {
        bool hasInline = entry.Persona is not null;
        bool hasFile = entry.PersonaFile is not null;

        // Task 3.3 / brief point 4: exactly one of persona / personaFile must be set.
        if (hasInline && hasFile)
        {
            throw new AgentProfileConfigException(
                $"Profile '{entry.Name}' specifies both 'persona' and 'personaFile'. " +
                $"Set exactly one.");
        }

        if (!hasInline && !hasFile)
        {
            throw new AgentProfileConfigException(
                $"Profile '{entry.Name}' specifies neither 'persona' nor 'personaFile'. " +
                $"Set exactly one.");
        }

        if (hasFile)
        {
            string path = entry.PersonaFile!;
            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new AgentProfileConfigException(
                    $"Profile '{entry.Name}': cannot read personaFile '{path}': {ex.Message}",
                    ex);
            }
        }

        return entry.Persona!;
    }

    private static string BuildAvailableList(
        Dictionary<string, ProfileConfigEntry> configProfiles,
        bool builtInCodingOverridden)
    {
        List<string> names = [.. configProfiles.Keys];

        if (!builtInCodingOverridden)
        {
            names.Add(BuiltInProfiles.CodingProfileName);
        }

        return names.Count == 0 ? "(none)" : string.Join(", ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }
}
