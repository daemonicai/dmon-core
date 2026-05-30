using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using LocalPackageInfo = NuGet.Repositories.LocalPackageInfo;
using NuGetv3LocalRepository = NuGet.Repositories.NuGetv3LocalRepository;

namespace Dmon.Runtime;

/// <summary>
/// Live implementation of <see cref="ICoreAcquisitionSource"/> backed by the global NuGet
/// packages cache and nuget.org via the NuGet.Protocol client SDK.
/// </summary>
internal sealed class NuGetCoreAcquisitionSource : ICoreAcquisitionSource
{
    internal const string PackageId = "dmoncore";
    private const string FeedUrl = "https://api.nuget.org/v3/index.json";

    private readonly string _globalPackagesFolder;
    private readonly SourceRepository _sourceRepository;
    private readonly SourceCacheContext _cacheContext;

    internal NuGetCoreAcquisitionSource()
    {
        ISettings settings = Settings.LoadDefaultSettings(null);
        _globalPackagesFolder = SettingsUtility.GetGlobalPackagesFolder(settings);

        PackageSource source = new(FeedUrl);
        _sourceRepository = Repository.Factory.GetCoreV3(source);
        _cacheContext = new SourceCacheContext();
    }

    public (NuGetVersion Version, string ExpandedPath)? TryGetCompatibleCachedVersion(
        string targetMajorMinor)
    {
        NuGetv3LocalRepository localRepo = new(_globalPackagesFolder);

        NuGetVersion? best = localRepo.FindPackagesById(PackageId)
            .Select(p => p.Version)
            .Where(v => Protocol.ProtocolVersion.MajorMinor(v.ToNormalizedString()) == targetMajorMinor)
            .OrderByDescending(v => v)
            .FirstOrDefault();

        if (best is null)
            return null;

        LocalPackageInfo? pkg = localRepo.FindPackage(PackageId, best);
        if (pkg is null)
            return null;

        string dllPath = Path.Combine(pkg.ExpandedPath, "dmoncore.dll");
        return File.Exists(dllPath)
            ? (best, pkg.ExpandedPath)
            : null;
    }

    public async Task<IReadOnlyList<NuGetVersion>> GetAllVersionsAsync(CancellationToken cancellationToken)
    {
        FindPackageByIdResource resource = await _sourceRepository
            .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
            .ConfigureAwait(false);

        IEnumerable<NuGetVersion> versions = await resource
            .GetAllVersionsAsync(PackageId, _cacheContext, NullLogger.Instance, cancellationToken)
            .ConfigureAwait(false);

        return versions.ToList();
    }

    public async Task<string> AcquireAsync(NuGetVersion version, CancellationToken cancellationToken)
    {
        FindPackageByIdResource resource = await _sourceRepository
            .GetResourceAsync<FindPackageByIdResource>(cancellationToken)
            .ConfigureAwait(false);

        PackageIdentity identity = new(PackageId, version);

        using MemoryStream nupkgStream = new();
        bool downloaded = await resource.CopyNupkgToStreamAsync(
            PackageId,
            version,
            nupkgStream,
            _cacheContext,
            NullLogger.Instance,
            cancellationToken).ConfigureAwait(false);

        if (!downloaded)
            throw new InvalidOperationException(
                $"Package {PackageId} {version} could not be downloaded from {FeedUrl}.");

        nupkgStream.Position = 0;

        using DownloadResourceResult result = await GlobalPackagesFolderUtility.AddPackageAsync(
            source: FeedUrl,
            packageIdentity: identity,
            packageStream: nupkgStream,
            globalPackagesFolder: _globalPackagesFolder,
            parentId: Guid.Empty,
            clientPolicyContext: null,
            logger: NullLogger.Instance,
            token: cancellationToken).ConfigureAwait(false);

        if (result.Status == DownloadResourceResultStatus.NotFound)
            throw new InvalidOperationException(
                $"Package {PackageId} {version} was not found after installation attempt.");

        string expandedPath = GetExpandedPath(identity)
            ?? throw new InvalidOperationException(
                $"Package {PackageId} {version} was installed but its expanded path could not be found.");

        return expandedPath;
    }

    private string? GetExpandedPath(PackageIdentity identity)
    {
        NuGetv3LocalRepository localRepo = new(_globalPackagesFolder);
        LocalPackageInfo? pkg = localRepo.FindPackage(identity.Id, identity.Version);
        return pkg?.ExpandedPath;
    }
}
