namespace Dmon.Core.Extensions.Security;

public interface IExtensionSourceFetcher
{
    /// <summary>
    /// Downloads the .nupkg for <paramref name="packageId"/> at <paramref name="version"/>,
    /// extracts the nuspec, validates source availability, and fetches .cs files at the
    /// recorded commit SHA.
    /// </summary>
    /// <exception cref="SourceNotAvailableException">
    /// Thrown if the nuspec has no &lt;repository url&gt; element, or if source cannot be fetched.
    /// </exception>
    Task<SourceFetchResult> FetchAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default);
}
