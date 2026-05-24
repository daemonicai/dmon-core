namespace Dmon.Core.Extensions.NuGet;

internal interface INuGetSearchService
{
    /// <summary>
    /// Searches NuGet for packages tagged <c>dmon-extension</c>, filters to source-available
    /// packages, optionally enriches with GitHub signals, ranks, and returns ≤5 results.
    /// Never throws — returns an empty list on any failure.
    /// </summary>
    Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken);
}
