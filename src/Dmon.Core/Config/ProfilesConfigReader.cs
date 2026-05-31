using Dmon.Abstractions.Profiles;
using Microsoft.Extensions.Configuration;

namespace Dmon.Core.Config;

/// <summary>
/// Reads the <c>profiles:</c> map from a single <c>config.yaml</c> file into
/// typed <see cref="ProfileConfigEntry"/> values.
/// </summary>
/// <remarks>
/// Intentionally builds its own <see cref="IConfigurationRoot"/> from the given
/// path rather than reading the shared, layered <c>IConfiguration</c>: the
/// layered provider would collapse map entries by key when both user and project
/// configs are present, silently discarding entries.
///
/// The <c>profiles:</c> section is a YAML map keyed by profile name:
/// <code>
/// profiles:
///   helper:
///     persona: "You are a helpful assistant."
///     assets: true
///     permissionMode: sandbox
/// </code>
/// Each map key becomes the profile <see cref="ProfileConfigEntry.Name"/>.
/// A <c>personaFile</c> path is resolved to an absolute, normalised path relative
/// to the directory of the config file that declared it.
/// </remarks>
public sealed class ProfilesConfigReader
{
    /// <summary>
    /// Reads profile entries and the <c>defaultProfile</c> scalar from
    /// <paramref name="configFilePath"/>.
    /// </summary>
    /// <param name="configFilePath">
    /// Absolute path to the <c>config.yaml</c> to read. A missing file returns
    /// an empty result; an absent <c>profiles:</c> section likewise returns empty.
    /// </param>
    /// <returns>A pair of the profile entries and the raw <c>defaultProfile</c> value (or <see langword="null"/>).</returns>
    public (IReadOnlyList<ProfileConfigEntry> Entries, string? DefaultProfile) Read(string configFilePath)
    {
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddYamlFile(configFilePath, optional: true, reloadOnChange: false)
            .Build();

        string? defaultProfile = config["defaultProfile"];

        IConfigurationSection profilesSection = config.GetSection("profiles");

        if (!profilesSection.Exists())
        {
            return ([], defaultProfile);
        }

        // Each child of profiles: is a named profile entry (the key is the profile name).
        string configDir = Path.GetDirectoryName(Path.GetFullPath(configFilePath))
            ?? Directory.GetCurrentDirectory();

        List<ProfileConfigEntry> result = [];

        foreach (IConfigurationSection entry in profilesSection.GetChildren())
        {
            string name = entry.Key;

            string? persona = entry["persona"];
            string? personaFileRaw = entry["personaFile"];

            string? personaFile = ResolvePersonaFile(personaFileRaw, configDir);

            bool assets = ParseBool(entry["assets"], name, "assets");

            PermissionMode permissionMode = ParsePermissionMode(entry["permissionMode"], name);

            result.Add(new ProfileConfigEntry(name, persona, personaFile, assets, permissionMode));
        }

        return (result, defaultProfile);
    }

    private static string? ResolvePersonaFile(string? raw, string configDir)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Resolve relative paths against the directory of the declaring config file.
        string combined = Path.IsPathRooted(raw)
            ? raw
            : Path.Combine(configDir, raw);

        return Path.GetFullPath(combined);
    }

    private static PermissionMode ParsePermissionMode(string? raw, string profileName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PermissionMode.Coding;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "coding" => PermissionMode.Coding,
            "sandbox" => PermissionMode.Sandbox,
            _ => throw new InvalidOperationException(
                $"Profile '{profileName}' has an invalid permissionMode '{raw.Trim()}'; expected 'coding' or 'sandbox'."),
        };
    }

    private static bool ParseBool(string? raw, string profileName, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (bool.TryParse(raw.Trim(), out bool value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Profile '{profileName}' has an invalid {fieldName} value '{raw.Trim()}'; expected 'true' or 'false'.");
    }
}
