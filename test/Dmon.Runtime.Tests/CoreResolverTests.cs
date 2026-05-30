using Dmon.Protocol;
using Dmon.Runtime;
using NuGet.Versioning;

namespace Dmon.Runtime.Tests;

/// <summary>
/// Covers tasks 3.3 / 3.4 / 3.7: discovery precedence and acquisition behaviour,
/// all offline (no network calls; no real NuGet cache).
/// </summary>
public sealed class CoreResolverTests : IDisposable
{
    // Temp directory that tests can place fake files in; cleaned up in Dispose.
    private readonly string _tempDir;

    public CoreResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dmon-resolver-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    // Tier 1: --core-path override wins over everything else
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_OverridePath_WinsOverCacheAndNetwork()
    {
        string overrideExe = Path.Combine(_tempDir, "override-dmoncore");
        await File.WriteAllTextAsync(overrideExe, "fake");

        FakeCoreAcquisitionSource source = new(
            networkVersions: [new NuGetVersion("0.1.0")],
            cachedEntry: (new NuGetVersion("0.1.0"), _tempDir));

        CoreResolver resolver = new(source);
        ResolvedCore result = await resolver.ResolveAsync(overrideExe, CancellationToken.None);

        Assert.Equal(LaunchMode.DirectExecutable, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(overrideExe), result.Path);
        Assert.False(source.GetAllVersionsCalled, "Network must not be consulted when override is set.");
    }

    // ------------------------------------------------------------------
    // Tier 2: DMON_CORE_PATH env var wins over cache and network
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_EnvVar_WinsOverCacheAndNetwork()
    {
        string envExe = Path.Combine(_tempDir, "env-dmoncore");
        await File.WriteAllTextAsync(envExe, "fake");

        string? original = Environment.GetEnvironmentVariable("DMON_CORE_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", envExe);

            FakeCoreAcquisitionSource source = new(
                networkVersions: [new NuGetVersion("0.1.0")],
                cachedEntry: (new NuGetVersion("0.1.0"), _tempDir));

            CoreResolver resolver = new(source);
            ResolvedCore result = await resolver.ResolveAsync(
                corePathOverride: null, CancellationToken.None);

            Assert.Equal(LaunchMode.DirectExecutable, result.LaunchMode);
            Assert.Equal(Path.GetFullPath(envExe), result.Path);
            Assert.False(source.GetAllVersionsCalled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DMON_CORE_PATH", original);
        }
    }

    // ------------------------------------------------------------------
    // Override wins even when cache has a compatible entry
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_OverridePath_WinsOverCompatibleCache()
    {
        string overrideExe = Path.Combine(_tempDir, "override-dmoncore");
        await File.WriteAllTextAsync(overrideExe, "fake");

        string cacheExpandedDir = Path.Combine(_tempDir, "cached");
        Directory.CreateDirectory(cacheExpandedDir);
        await File.WriteAllTextAsync(Path.Combine(cacheExpandedDir, "dmoncore.dll"), "fake");

        FakeCoreAcquisitionSource source = new(
            cachedEntry: (new NuGetVersion("0.1.0"), cacheExpandedDir));

        CoreResolver resolver = new(source);
        ResolvedCore result = await resolver.ResolveAsync(overrideExe, CancellationToken.None);

        Assert.Equal(LaunchMode.DirectExecutable, result.LaunchMode);
        Assert.Equal(Path.GetFullPath(overrideExe), result.Path);
    }

    // ------------------------------------------------------------------
    // Tier 3: cache hit — no network call
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_CacheHit_DoesNotCallNetwork()
    {
        string cacheExpandedDir = Path.Combine(_tempDir, "cached");
        Directory.CreateDirectory(cacheExpandedDir);
        await File.WriteAllTextAsync(Path.Combine(cacheExpandedDir, "dmoncore.dll"), "fake");

        NuGetVersion cachedVersion = new("0.1.5");
        FakeCoreAcquisitionSource source = new(
            cachedEntry: (cachedVersion, cacheExpandedDir));

        CoreResolver resolver = new(source);
        ResolvedCore result = await resolver.ResolveAsync(
            corePathOverride: null, CancellationToken.None);

        Assert.Equal(LaunchMode.DotnetExec, result.LaunchMode);
        Assert.Contains("dmoncore.dll", result.Path);
        Assert.False(source.GetAllVersionsCalled, "Network must not be hit when cache has a compatible version.");
        Assert.False(source.AcquireCalled);
    }

    // ------------------------------------------------------------------
    // Tier 4: no cache → fetch; version filter selects newest matching Major.Minor
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NoCache_FetchesAndPicks_NewestCompatible()
    {
        string targetMajorMinor = ProtocolVersion.MajorMinor(ProtocolVersion.Current)!;

        // Build a version list: one older compatible, one newer compatible, one incompatible.
        NuGetVersion olderCompatible = NuGetVersion.Parse($"{targetMajorMinor}.1");
        NuGetVersion newerCompatible = NuGetVersion.Parse($"{targetMajorMinor}.9");
        NuGetVersion incompatible = NuGetVersion.Parse("99.99.0");

        string acquiredDir = Path.Combine(_tempDir, "acquired");
        Directory.CreateDirectory(acquiredDir);
        await File.WriteAllTextAsync(Path.Combine(acquiredDir, "dmoncore.dll"), "fake");

        FakeCoreAcquisitionSource source = new(
            networkVersions: [olderCompatible, newerCompatible, incompatible],
            acquiredExpandedPath: acquiredDir);

        CoreResolver resolver = new(source);
        ResolvedCore result = await resolver.ResolveAsync(
            corePathOverride: null, CancellationToken.None);

        Assert.Equal(LaunchMode.DotnetExec, result.LaunchMode);
        Assert.True(source.AcquireCalled);
        Assert.Equal(newerCompatible, source.LastAcquiredVersion);
    }

    // ------------------------------------------------------------------
    // Version filter: incompatible Major.Minor is never selected
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NoCompatibleVersion_ThrowsCoreAcquisitionException()
    {
        FakeCoreAcquisitionSource source = new(
            networkVersions: [NuGetVersion.Parse("99.99.0"), NuGetVersion.Parse("98.1.0")]);

        CoreResolver resolver = new(source);

        CoreAcquisitionException ex = await Assert.ThrowsAsync<CoreAcquisitionException>(
            () => resolver.ResolveAsync(corePathOverride: null, CancellationToken.None));

        Assert.Contains(ProtocolVersion.Current, ex.Message);
    }

    // ------------------------------------------------------------------
    // Acquisition failure → actionable error naming the overrides
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NetworkFailure_ThrowsActionableError()
    {
        string targetMajorMinor = ProtocolVersion.MajorMinor(ProtocolVersion.Current)!;
        NuGetVersion compatible = NuGetVersion.Parse($"{targetMajorMinor}.0");

        // GetAllVersions succeeds, but AcquireAsync throws.
        FakeCoreAcquisitionSource source = new(
            networkVersions: [compatible],
            networkException: new HttpRequestException("Network unavailable"));

        CoreResolver resolver = new(source);

        // GetAllVersionsAsync will throw because the fake always throws on network exception.
        CoreAcquisitionException ex = await Assert.ThrowsAsync<CoreAcquisitionException>(
            () => resolver.ResolveAsync(corePathOverride: null, CancellationToken.None));

        Assert.Contains("--core-path", ex.Message);
        Assert.Contains("DMON_CORE_PATH", ex.Message);
    }

    // ------------------------------------------------------------------
    // Acquisition failure from AcquireAsync specifically
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_AcquireFailure_ThrowsActionableError()
    {
        string targetMajorMinor = ProtocolVersion.MajorMinor(ProtocolVersion.Current)!;
        NuGetVersion compatible = NuGetVersion.Parse($"{targetMajorMinor}.0");

        // Separate fakes: versions come back fine, acquire throws.
        PartialFailureCoreAcquisitionSource source = new(
            networkVersions: [compatible],
            acquireException: new HttpRequestException("Connection refused"));

        CoreResolver resolver = new(source);

        CoreAcquisitionException ex = await Assert.ThrowsAsync<CoreAcquisitionException>(
            () => resolver.ResolveAsync(corePathOverride: null, CancellationToken.None));

        Assert.Contains("--core-path", ex.Message);
        Assert.Contains("DMON_CORE_PATH", ex.Message);
    }
}

/// <summary>
/// Variant fake where GetAllVersionsAsync succeeds but AcquireAsync throws.
/// Needed to test the separate error path in <see cref="CoreResolver"/>.
/// </summary>
internal sealed class PartialFailureCoreAcquisitionSource : ICoreAcquisitionSource
{
    private readonly IReadOnlyList<NuGetVersion> _networkVersions;
    private readonly Exception _acquireException;

    public PartialFailureCoreAcquisitionSource(
        IReadOnlyList<NuGetVersion> networkVersions,
        Exception acquireException)
    {
        _networkVersions = networkVersions;
        _acquireException = acquireException;
    }

    public (NuGetVersion Version, string ExpandedPath)? TryGetCompatibleCachedVersion(
        string targetMajorMinor) => null;

    public Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(CancellationToken cancellationToken)
        => Task.FromResult(_networkVersions);

    public Task<string> AcquireAsync(NuGetVersion version, CancellationToken cancellationToken)
        => throw _acquireException;
}
