using Dmon.Protocol;
using NuGet.Versioning;

namespace Dmon.Runtime;

/// <summary>
/// Resolves the dmoncore binary through the fixed discovery precedence:
/// (1) --core-path override, (2) DMON_CORE_PATH env var,
/// (3) global NuGet cache, (4) on-demand acquisition.
/// </summary>
internal sealed class CoreResolver
{
    private readonly ICoreAcquisitionSource _source;

    internal CoreResolver(ICoreAcquisitionSource source)
    {
        _source = source;
    }

    /// <summary>
    /// Resolves the core, acquiring it from nuget.org if necessary.
    /// </summary>
    /// <param name="corePathOverride">
    /// Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    internal async Task<ResolvedCore> ResolveAsync(
        string? corePathOverride,
        CancellationToken cancellationToken)
    {
        // Tier 1: --core-path explicit override.
        if (!string.IsNullOrEmpty(corePathOverride) && File.Exists(corePathOverride))
            return new ResolvedCore(Path.GetFullPath(corePathOverride), LaunchMode.DirectExecutable);

        // Tier 2: DMON_CORE_PATH environment variable.
        string? envPath = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return new ResolvedCore(Path.GetFullPath(envPath), LaunchMode.DirectExecutable);

        // Tier 2b: published sibling layout (make build output: dmoncore/ next to dmon).
        // Tier 2c: dev bin layout (src/Dmon.Core/bin/<Config>/net10.0/dmoncore).
        // Both are apphost executables launched directly.
        string entryDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetEntryAssembly()?.Location) ?? ".";

        string publishedSibling = Path.Combine(entryDir, "dmoncore", "dmoncore");
        if (File.Exists(publishedSibling))
            return new ResolvedCore(Path.GetFullPath(publishedSibling), LaunchMode.DirectExecutable);

        string repoRoot = Path.GetFullPath(Path.Combine(entryDir, "../../../.."));
        string[] devCandidates =
        [
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Debug/net10.0/dmoncore"),
            Path.Combine(repoRoot, "src/Dmon.Core/bin/Release/net10.0/dmoncore"),
        ];
        foreach (string candidate in devCandidates)
        {
            if (File.Exists(candidate))
                return new ResolvedCore(Path.GetFullPath(candidate), LaunchMode.DirectExecutable);
        }

        // Tier 3: global NuGet cache — newest compatible cached version.
        string targetMajorMinor = ProtocolVersion.MajorMinor(ProtocolVersion.Current)
            ?? throw new InvalidOperationException(
                $"ProtocolVersion.Current ('{ProtocolVersion.Current}') could not be parsed.");

        (NuGetVersion cachedVersion, string expandedPath)? cached =
            _source.TryGetCompatibleCachedVersion(targetMajorMinor);

        if (cached is not null)
        {
            string cachedDll = Path.Combine(cached.Value.expandedPath, "dmoncore.dll");
            return new ResolvedCore(cachedDll, LaunchMode.DotnetExec);
        }

        // Tier 4: on-demand acquisition from nuget.org.
        return await AcquireAsync(targetMajorMinor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResolvedCore> AcquireAsync(
        string targetMajorMinor,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NuGetVersion> allVersions;
        try
        {
            allVersions = await _source
                .GetAllVersionsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CoreAcquisitionException(
                $"Failed to list available dmoncore versions from nuget.org: {ex.Message}\n" +
                "To work offline, set DMON_CORE_PATH or pass --core-path pointing at a dmoncore binary.",
                ex);
        }

        NuGetVersion? chosen = allVersions
            .Where(v => ProtocolVersion.MajorMinor(v.ToNormalizedString()) == targetMajorMinor)
            .OrderByDescending(v => v)
            .FirstOrDefault();

        if (chosen is null)
            throw new CoreAcquisitionException(
                $"No dmoncore package with protocol version {ProtocolVersion.Current} is available on nuget.org.\n" +
                "To work offline, set DMON_CORE_PATH or pass --core-path pointing at a dmoncore binary.");

        string expandedPath;
        try
        {
            expandedPath = await _source
                .AcquireAsync(chosen, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CoreAcquisitionException(
                $"Failed to download dmoncore {chosen} from nuget.org: {ex.Message}\n" +
                "To work offline, set DMON_CORE_PATH or pass --core-path pointing at a dmoncore binary.",
                ex);
        }

        string dllPath = Path.Combine(expandedPath, "dmoncore.dll");
        if (!File.Exists(dllPath))
            throw new CoreAcquisitionException(
                $"Package dmoncore {chosen} was downloaded but dmoncore.dll was not found in the publish closure at '{expandedPath}'.");

        return new ResolvedCore(dllPath, LaunchMode.DotnetExec);
    }
}
