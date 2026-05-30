using Dmon.Runtime;
using NuGet.Versioning;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Fake <see cref="ICoreAcquisitionSource"/> for offline unit tests.
/// Controls what the cache contains and what versions the network returns.
/// </summary>
internal sealed class FakeCoreAcquisitionSource : ICoreAcquisitionSource
{
    private readonly IReadOnlyList<NuGetVersion> _networkVersions;
    private readonly string _acquiredExpandedPath;
    private readonly Exception? _networkException;
    private readonly (NuGetVersion Version, string ExpandedPath)? _cachedEntry;

    public bool GetAllVersionsCalled { get; private set; }
    public bool AcquireCalled { get; private set; }
    public NuGetVersion? LastAcquiredVersion { get; private set; }

    public FakeCoreAcquisitionSource(
        IReadOnlyList<NuGetVersion>? networkVersions = null,
        string acquiredExpandedPath = "/fake/packages/dmoncore/1.0.0",
        Exception? networkException = null,
        (NuGetVersion Version, string ExpandedPath)? cachedEntry = null)
    {
        _networkVersions = networkVersions ?? [];
        _acquiredExpandedPath = acquiredExpandedPath;
        _networkException = networkException;
        _cachedEntry = cachedEntry;
    }

    public (NuGetVersion Version, string ExpandedPath)? TryGetCompatibleCachedVersion(
        string targetMajorMinor)
    {
        if (_cachedEntry is null)
            return null;

        // Check if the cached entry matches the target protocol version.
        string? entryMajorMinor = Protocol.ProtocolVersion.MajorMinor(
            _cachedEntry.Value.Version.ToNormalizedString());

        return entryMajorMinor == targetMajorMinor ? _cachedEntry : null;
    }

    public Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(CancellationToken cancellationToken)
    {
        GetAllVersionsCalled = true;
        if (_networkException is not null)
            throw _networkException;
        return Task.FromResult(_networkVersions);
    }

    public Task<string> AcquireAsync(NuGetVersion version, CancellationToken cancellationToken)
    {
        AcquireCalled = true;
        LastAcquiredVersion = version;
        if (_networkException is not null)
            throw _networkException;
        return Task.FromResult(_acquiredExpandedPath);
    }
}
