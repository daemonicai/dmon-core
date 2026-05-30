using NuGet.Versioning;

namespace Dmon.Runtime;

/// <summary>
/// Seam over NuGet cache and network operations so discovery logic can be tested without
/// hitting the filesystem cache or nuget.org.
/// </summary>
internal interface ICoreAcquisitionSource
{
    /// <summary>
    /// Returns the newest compatible version present in the local cache, and the path to its
    /// expanded publish folder (the directory containing <c>dmoncore.dll</c>), or
    /// <see langword="null"/> if no compatible version is cached.
    /// </summary>
    (NuGetVersion Version, string ExpandedPath)? TryGetCompatibleCachedVersion(string targetMajorMinor);

    /// <summary>
    /// Returns all available versions of the <c>dmoncore</c> package from the upstream source.
    /// </summary>
    Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the <c>dmoncore</c> package at <paramref name="version"/> and installs it
    /// into the global NuGet packages folder, returning the expanded path (the directory
    /// that contains <c>dmoncore.dll</c>).
    /// </summary>
    Task<string> AcquireAsync(NuGetVersion version, CancellationToken cancellationToken);
}
