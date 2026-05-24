namespace Dmon.Core.GitHub;

/// <summary>
/// Probes whether the gh CLI is installed and authenticated.
/// Result is cached for the lifetime of the service instance.
/// </summary>
public interface IGhCliService
{
    /// <summary>
    /// Returns <see langword="true"/> if <c>gh</c> is installed and authenticated;
    /// <see langword="false"/> if not installed or not authenticated.
    /// Never throws — degradation is the contract.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}
