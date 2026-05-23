using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

/// <summary>
/// Result of loading a single extension source (NuGet package, local assembly, or .csx script).
/// </summary>
public sealed record ExtensionLoadResult
{
    /// <summary>
    /// The name under which this extension is registered in the tool registry.
    /// For NuGet/local assemblies, this is the first <see cref="IDmonExtension.Name"/>.
    /// For .csx scripts, derived from the script filename.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The human-readable description from <see cref="IDmonExtension.Description"/>
    /// or a default for scripts.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The <see cref="AIFunction"/> instances to register.
    /// </summary>
    public required IReadOnlyList<AIFunction> Tools { get; init; }

    /// <summary>
    /// Source kind used for permission-confirmation prompts.
    /// </summary>
    public required string SourceKind { get; init; } // "nuget", "assembly", "script"
}

/// <summary>
/// Parsed form of an <c>extension.load {source}</c> command.
/// </summary>
public sealed record ParsedExtensionSource
{
    /// <summary>
    /// Source kind: "nuget", "assembly", or "script".
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// For "nuget": package id (e.g. "MyExtension").
    /// For "assembly" and "script": the file path.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// For "nuget": optional version string. Null means latest.
    /// </summary>
    public string? Version { get; init; }

    private static readonly char[] Separators = ['/', '\\'];

    public static ParsedExtensionSource Parse(string source)
    {
        if (source.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = source[6..];
            int slash = rest.IndexOf('/');

            if (slash >= 0)
            {
                return new ParsedExtensionSource
                {
                    Kind = "nuget",
                    Value = rest[..slash],
                    Version = rest[(slash + 1)..]
                };
            }

            return new ParsedExtensionSource
            {
                Kind = "nuget",
                Value = rest,
                Version = null
            };
        }

        if (source.EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedExtensionSource
            {
                Kind = "script",
                Value = source
            };
        }

        if (source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || source.IndexOfAny(Separators) >= 0)
        {
            return new ParsedExtensionSource
            {
                Kind = "assembly",
                Value = source
            };
        }

        // Default: treat as NuGet package id without the "nuget:" prefix.
        return new ParsedExtensionSource
        {
            Kind = "nuget",
            Value = source,
            Version = null
        };
    }
}