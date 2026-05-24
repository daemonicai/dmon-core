using Dmon.Core.Extensions.Security;
using Dmon.Core.GitHub;
using Xunit;

namespace Dmon.Core.Tests.Extensions.Security;

public sealed class ExtensionSourceFetcherIntegrationTests
{
    [Fact(Skip = "Integration test — requires internet access. Remove Skip to run manually.")]
    public async Task FetchAsync_KnownPublicPackage_ReturnsSources()
    {
        // Serilog 4.1.0 has Source Link / <repository url> in its nuspec.
        // Adjust package and version as needed if this version is no longer available.
        const string packageId = "Serilog";
        const string version = "4.1.0";

        using HttpClient httpClient = new();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("dmon/1.0");

        GhCliService ghCliService = new();
        ExtensionSourceFetcher fetcher = new(httpClient, ghCliService);

        SourceFetchResult result = await fetcher.FetchAsync(packageId, version);

        Assert.Equal(packageId, result.PackageId);
        Assert.Equal(version, result.Version);
        Assert.NotEmpty(result.RepositoryUrl);
        Assert.NotEmpty(result.CommitSha);
        Assert.NotEmpty(result.SourceFiles);
        Assert.All(result.SourceFiles.Keys, k => Assert.EndsWith(".cs", k, StringComparison.OrdinalIgnoreCase));
    }
}
