using Dmon.Core.Extensions;

namespace Dmon.Core.Config;

/// <summary>
/// Produces a canonical dedup key for an extension source string.
/// Used by <see cref="EffectiveExtensionSetResolver"/> to identify duplicate entries
/// across user and project config files.
/// </summary>
public static class ExtensionSourceNormalizer
{
    /// <summary>
    /// Returns a normalized key for <paramref name="source"/> suitable for dedup comparison.
    /// </summary>
    /// <remarks>
    /// Nuget: <c>nuget:&lt;id-lower&gt;@&lt;version-lower-or-empty&gt;</c>.
    /// Different inline versions are distinct keys, so <c>nuget:Acme/1.0</c> and
    /// <c>nuget:Acme/2.0</c> do not collide.
    /// Assembly/script (paths): <c>&lt;kind&gt;:&lt;normalised-path&gt;</c> where the path
    /// has backslashes replaced with forward slashes, a single trailing slash trimmed, and
    /// is lowercased. Absolute-path resolution is intentionally NOT performed — that would
    /// depend on CWD and break the pure unit tests.
    /// </remarks>
    public static string Normalize(string source)
    {
        // Trim trailing slashes before parsing so that "path/my.csx/" is
        // recognised as a script, not misclassified as an assembly.
        string trimmed = source.TrimEnd('/', '\\');
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse(
            string.IsNullOrEmpty(trimmed) ? source : trimmed);

        if (parsed.Kind == "nuget")
        {
            string id = parsed.Value.ToLowerInvariant();
            string version = parsed.Version?.ToLowerInvariant() ?? "";
            return $"nuget:{id}@{version}";
        }

        // assembly or script: normalize path portion only
        string path = parsed.Value
            .Trim()
            .Replace('\\', '/')
            .TrimEnd('/');

        return $"{parsed.Kind}:{path.ToLowerInvariant()}";
    }
}
