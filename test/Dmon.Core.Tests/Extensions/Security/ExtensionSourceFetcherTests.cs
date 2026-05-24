using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml.Linq;
using Dmon.Core.Extensions.Security;
using Dmon.Core.GitHub;
using Xunit;

namespace Dmon.Core.Tests.Extensions.Security;

public sealed class ExtensionSourceFetcherTests
{
    private const string GitHubTreeJson = """
        {
          "tree": [
            { "path": "src/Foo.cs", "type": "blob" },
            { "path": "src/Bar.cs", "type": "blob" },
            { "path": "README.md", "type": "blob" }
          ]
        }
        """;

    private static byte[] BuildNupkg(string? repoUrl, string? commitSha = "abc123def456")
    {
        using MemoryStream ms = new();
        using (ZipArchive zip = new(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry nuspecEntry = zip.CreateEntry("test.package.1.0.0.nuspec");
            using Stream nuspecStream = nuspecEntry.Open();

            XNamespace ns = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";
            XElement metadata = new(ns + "metadata",
                new XElement(ns + "id", "Test.Package"),
                new XElement(ns + "version", "1.0.0"));

            if (repoUrl is not null)
            {
                XElement repo = new(ns + "repository");
                repo.SetAttributeValue("url", repoUrl);
                if (commitSha is not null)
                    repo.SetAttributeValue("commit", commitSha);
                metadata.Add(repo);
            }

            XDocument nuspec = new(new XElement(ns + "package", metadata));
            nuspec.Save(nuspecStream);
        }
        // Flush is automatic when ZipArchive is disposed (via using block above)
        return ms.ToArray();
    }

    [Fact]
    public async Task FetchAsync_MissingRepositoryElement_ThrowsSourceNotAvailable()
    {
        byte[] nupkg = BuildNupkg(repoUrl: null);
        FakeHttpMessageHandler handler = new(new Dictionary<string, (HttpStatusCode, byte[]?)>
        {
            ["https://api.nuget.org/v3-flatcontainer/test.package/1.0.0/test.package.1.0.0.nupkg"] =
                (HttpStatusCode.OK, nupkg),
        });

        using HttpClient httpClient = new(handler);
        ExtensionSourceFetcher fetcher = new(httpClient, new AlwaysUnavailableGhCliService());

        SourceNotAvailableException ex = await Assert.ThrowsAsync<SourceNotAvailableException>(
            () => fetcher.FetchAsync("Test.Package", "1.0.0"));

        Assert.Contains("repository", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_NonGitHubRepositoryUrl_ThrowsSourceNotAvailable()
    {
        byte[] nupkg = BuildNupkg("https://gitlab.com/owner/repo");
        FakeHttpMessageHandler handler = new(new Dictionary<string, (HttpStatusCode, byte[]?)>
        {
            ["https://api.nuget.org/v3-flatcontainer/test.package/1.0.0/test.package.1.0.0.nupkg"] =
                (HttpStatusCode.OK, nupkg),
        });

        using HttpClient httpClient = new(handler);
        ExtensionSourceFetcher fetcher = new(httpClient, new AlwaysUnavailableGhCliService());

        SourceNotAvailableException ex = await Assert.ThrowsAsync<SourceNotAvailableException>(
            () => fetcher.FetchAsync("Test.Package", "1.0.0"));

        Assert.Contains("GitHub", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_FileCountExceedsMax_ThrowsSourceNotAvailable()
    {
        byte[] nupkg = BuildNupkg("https://github.com/owner/repo");

        // Build a tree with MaxSourceFiles + 1 .cs files
        int fileCount = ExtensionSourceFetcher.MaxSourceFiles + 1;
        StringBuilder treeJson = new();
        treeJson.Append("{\"tree\":[");
        for (int i = 0; i < fileCount; i++)
        {
            if (i > 0) treeJson.Append(',');
            treeJson.Append($"{{\"path\":\"src/File{i}.cs\",\"type\":\"blob\"}}");
        }
        treeJson.Append("]}");

        FakeHttpMessageHandler handler = new(new Dictionary<string, (HttpStatusCode, byte[]?)>
        {
            ["https://api.nuget.org/v3-flatcontainer/test.package/1.0.0/test.package.1.0.0.nupkg"] =
                (HttpStatusCode.OK, nupkg),
            ["https://api.github.com/repos/owner/repo/git/trees/abc123def456?recursive=1"] =
                (HttpStatusCode.OK, null),
        }, overrideText: new Dictionary<string, string>
        {
            ["https://api.github.com/repos/owner/repo/git/trees/abc123def456?recursive=1"] = treeJson.ToString(),
        });

        using HttpClient httpClient = new(handler);
        ExtensionSourceFetcher fetcher = new(httpClient, new AlwaysUnavailableGhCliService());

        SourceNotAvailableException ex = await Assert.ThrowsAsync<SourceNotAvailableException>(
            () => fetcher.FetchAsync("Test.Package", "1.0.0"));

        Assert.Contains("size limits", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_TotalBytesExceedsMax_ThrowsSourceNotAvailable()
    {
        byte[] nupkg = BuildNupkg("https://github.com/owner/repo");

        // Two .cs files, each slightly over half the limit
        string largeContent = new('x', ExtensionSourceFetcher.MaxTotalBytes / 2 + 1024);

        FakeHttpMessageHandler handler = new(new Dictionary<string, (HttpStatusCode, byte[]?)>
        {
            ["https://api.nuget.org/v3-flatcontainer/test.package/1.0.0/test.package.1.0.0.nupkg"] =
                (HttpStatusCode.OK, nupkg),
        }, overrideText: new Dictionary<string, string>
        {
            ["https://api.github.com/repos/owner/repo/git/trees/abc123def456?recursive=1"] =
                "{\"tree\":[{\"path\":\"A.cs\",\"type\":\"blob\"},{\"path\":\"B.cs\",\"type\":\"blob\"}]}",
            ["https://raw.githubusercontent.com/owner/repo/abc123def456/A.cs"] = largeContent,
            ["https://raw.githubusercontent.com/owner/repo/abc123def456/B.cs"] = largeContent,
        });

        using HttpClient httpClient = new(handler);
        ExtensionSourceFetcher fetcher = new(httpClient, new AlwaysUnavailableGhCliService());

        SourceNotAvailableException ex = await Assert.ThrowsAsync<SourceNotAvailableException>(
            () => fetcher.FetchAsync("Test.Package", "1.0.0"));

        Assert.Contains("size limits", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_ValidNuspecAndFiles_ReturnsCorrectResult()
    {
        byte[] nupkg = BuildNupkg("https://github.com/owner/repo");

        FakeHttpMessageHandler handler = new(new Dictionary<string, (HttpStatusCode, byte[]?)>
        {
            ["https://api.nuget.org/v3-flatcontainer/test.package/1.0.0/test.package.1.0.0.nupkg"] =
                (HttpStatusCode.OK, nupkg),
        }, overrideText: new Dictionary<string, string>
        {
            ["https://api.github.com/repos/owner/repo/git/trees/abc123def456?recursive=1"] =
                "{\"tree\":[{\"path\":\"src/Foo.cs\",\"type\":\"blob\"}]}",
            ["https://raw.githubusercontent.com/owner/repo/abc123def456/src/Foo.cs"] =
                "public class Foo {}",
        });

        using HttpClient httpClient = new(handler);
        ExtensionSourceFetcher fetcher = new(httpClient, new AlwaysUnavailableGhCliService());

        SourceFetchResult result = await fetcher.FetchAsync("Test.Package", "1.0.0");

        Assert.Equal("Test.Package", result.PackageId);
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("https://github.com/owner/repo", result.RepositoryUrl);
        Assert.Equal("abc123def456", result.CommitSha);
        Assert.Single(result.SourceFiles);
        Assert.True(result.SourceFiles.ContainsKey("src/Foo.cs"));
        Assert.Equal("public class Foo {}", result.SourceFiles["src/Foo.cs"]);
    }
}

// ---------------------------------------------------------------------------
// Test infrastructure
// ---------------------------------------------------------------------------

/// <summary>Fake IGhCliService that always returns false (gh not available).</summary>
file sealed class AlwaysUnavailableGhCliService : IGhCliService
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        => Task.FromResult(false);
}

/// <summary>
/// Fake HttpMessageHandler.
/// Binary responses (byte[]): used for .nupkg and other binary content.
/// Text overrides (string): used for JSON API responses.
/// Resolution order: overrideText first, then binary responses dictionary.
/// </summary>
file sealed class FakeHttpMessageHandler(
    Dictionary<string, (HttpStatusCode Status, byte[]? Bytes)> binaryResponses,
    Dictionary<string, string>? overrideText = null) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.ToString();

        // Check text overrides first (exact match)
        if (overrideText is not null && overrideText.TryGetValue(url, out string? text))
        {
            HttpContent textContent = new StringContent(text, Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = textContent });
        }

        // Try binary responses (exact match)
        if (!binaryResponses.TryGetValue(url, out (HttpStatusCode Status, byte[]? Bytes) entry))
        {
            // Try matching ignoring query string
            string urlWithoutQuery = url.Split('?')[0];
            string? matchKey = binaryResponses.Keys.FirstOrDefault(k => k.Split('?')[0] == urlWithoutQuery);
            if (matchKey is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            entry = binaryResponses[matchKey];
        }

        HttpContent content = entry.Bytes is not null
            ? new ByteArrayContent(entry.Bytes)
            : new StringContent(string.Empty);

        return Task.FromResult(new HttpResponseMessage(entry.Status) { Content = content });
    }
}
