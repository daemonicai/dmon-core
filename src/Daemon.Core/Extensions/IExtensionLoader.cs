using Daemon.Protocol.Events;

namespace Daemon.Core.Extensions;

/// <summary>
/// Loads an extension from a given source kind. Each implementation handles
/// one source type: NuGet packages, local assemblies, or .csx scripts.
/// </summary>
public interface IExtensionLoader
{
    /// <summary>
    /// The source kind this loader handles (e.g. "nuget", "assembly", "script").
    /// </summary>
    string SourceKind { get; }

    /// <summary>
    /// Permission-confirmation callback. Called BEFORE any network access or
    /// assembly load. The callback receives an <see cref="ExtensionErrorEvent"/>
    /// shape describing the source and risk, and returns true if loading should proceed.
    /// </summary>
    Func<ExtensionLoadConfirmRequest, CancellationToken, Task<bool>>? ConfirmCallback { get; set; }

    /// <summary>
    /// Loads and returns the extension's tool list. Must call <see cref="ConfirmCallback"/>
    /// before any network or file-system load that introduces new code into the process.
    /// </summary>
    Task<ExtensionLoadResult> LoadAsync(
        ParsedExtensionSource source,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Permission confirmation request for extension loading. Sent to the permission
/// gate before network access or assembly load.
/// </summary>
public sealed record ExtensionLoadConfirmRequest
{
    public required string Source { get; init; }
    public required string Phase { get; init; } // "load" or "resolve"
    public string? PackageId { get; init; }
    public string? PackageVersion { get; init; }
    public required string Description { get; init; }
}