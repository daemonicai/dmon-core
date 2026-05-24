using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dmon.Core.GitHub;

namespace Dmon.Core.Extensions.Security;

internal sealed class ExtensionSourceFetcher : IExtensionSourceFetcher
{
    public const int MaxSourceFiles = 100;
    public const int MaxTotalBytes = 512 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IGhCliService _ghCliService;

    public ExtensionSourceFetcher(HttpClient httpClient, IGhCliService ghCliService)
    {
        _httpClient = httpClient;
        _ghCliService = ghCliService;
    }

    public async Task<SourceFetchResult> FetchAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken = default)
    {
        string idLower = packageId.ToLowerInvariant();
        string versionLower = version.ToLowerInvariant();
        string nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg";

        byte[] nupkgBytes = await DownloadBytesAsync(nupkgUrl, cancellationToken);

        (string repoUrl, string commitSha) = ExtractRepositoryInfo(nupkgBytes, packageId);

        (string owner, string repo) = ParseGitHubOwnerRepo(repoUrl);

        IReadOnlyList<string> csPaths = await FetchCsFilePathsAsync(owner, repo, commitSha, cancellationToken);

        if (csPaths.Count > MaxSourceFiles)
            throw new SourceNotAvailableException(
                "Extension source exceeds size limits (100 files / 512 KB). Source analysis is not supported for this package.");

        Dictionary<string, string> sourceFiles = await FetchSourceFilesAsync(owner, repo, commitSha, csPaths, cancellationToken);

        return new SourceFetchResult
        {
            PackageId = packageId,
            Version = version,
            RepositoryUrl = repoUrl,
            CommitSha = commitSha,
            SourceFiles = sourceFiles,
        };
    }

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SourceNotAvailableException(
                $"Failed to download package from NuGet (HTTP {(int)response.StatusCode}).");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static (string repoUrl, string commitSha) ExtractRepositoryInfo(byte[] nupkgBytes, string packageId)
    {
        using MemoryStream ms = new(nupkgBytes);
        using ZipArchive zip = new(ms, ZipArchiveMode.Read);

        ZipArchiveEntry? nuspecEntry = zip.Entries.FirstOrDefault(e =>
            e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        if (nuspecEntry is null)
            throw new SourceNotAvailableException(
                $"No .nuspec entry found in package '{packageId}'.");

        XDocument nuspec;
        using (Stream nuspecStream = nuspecEntry.Open())
        {
            nuspec = XDocument.Load(nuspecStream);
        }

        XNamespace ns = nuspec.Root?.Name.Namespace ?? XNamespace.None;
        XElement? repositoryElement = nuspec.Root
            ?.Element(ns + "metadata")
            ?.Element(ns + "repository");

        if (repositoryElement is null)
            throw new SourceNotAvailableException(
                $"Package '{packageId}' has no <repository> element in its nuspec. Source analysis requires a package with Source Link.");

        string? repoUrl = repositoryElement.Attribute("url")?.Value;
        if (string.IsNullOrWhiteSpace(repoUrl))
            throw new SourceNotAvailableException(
                $"Package '{packageId}' has an empty <repository url> in its nuspec.");

        string? commitSha = repositoryElement.Attribute("commit")?.Value;
        if (string.IsNullOrWhiteSpace(commitSha))
            throw new SourceNotAvailableException(
                $"Package '{packageId}' has no commit SHA in its <repository> element. Source analysis requires a commit-pinned package.");

        return (repoUrl, commitSha);
    }

    private static (string owner, string repo) ParseGitHubOwnerRepo(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out Uri? uri))
            throw new SourceNotAvailableException(
                $"Repository URL '{repoUrl}' is not a valid URI.");

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new SourceNotAvailableException(
                "Only GitHub-hosted extensions are supported in V1.");

        // Segments: ['/', '{owner}/', '{repo}'] or ['/', '{owner}/', '{repo}.git']
        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2 || string.IsNullOrEmpty(segments[0]) || string.IsNullOrEmpty(segments[1]))
            throw new SourceNotAvailableException(
                $"Could not extract owner/repo from GitHub URL '{repoUrl}'.");

        string owner = segments[0];
        string repo = segments[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        return (owner, repo);
    }

    private async Task<IReadOnlyList<string>> FetchCsFilePathsAsync(
        string owner,
        string repo,
        string commitSha,
        CancellationToken cancellationToken)
    {
        // Try gh CLI first if available
        bool ghAvailable = await _ghCliService.IsAvailableAsync(cancellationToken);
        if (ghAvailable)
        {
            IReadOnlyList<string>? paths = await TryFetchViaGhAsync(owner, repo, commitSha, cancellationToken);
            if (paths is not null)
                return paths;
        }

        // Fall back to GitHub API via HttpClient (works for public repos)
        return await FetchViaGitHubApiAsync(owner, repo, commitSha, cancellationToken);
    }

    private async Task<IReadOnlyList<string>?> TryFetchViaGhAsync(
        string owner,
        string repo,
        string commitSha,
        CancellationToken cancellationToken)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new("gh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("api");
            psi.ArgumentList.Add($"/repos/{owner}/{repo}/git/trees/{commitSha}?recursive=1");
            psi.ArgumentList.Add("--jq");
            psi.ArgumentList.Add("[.tree[] | select(.path | endswith(\".cs\")) | .path]");

            using System.Diagnostics.Process process = new() { StartInfo = psi };
            process.Start();

            string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode != 0)
                return null;

            JsonNode? node = JsonNode.Parse(stdout.Trim());
            if (node is not JsonArray array)
                return null;

            return array.Select(n => n?.GetValue<string>() ?? string.Empty)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .ToList();
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> FetchViaGitHubApiAsync(
        string owner,
        string repo,
        string commitSha,
        CancellationToken cancellationToken)
    {
        string url = $"https://api.github.com/repos/{owner}/{repo}/git/trees/{commitSha}?recursive=1";

        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("dmon/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new SourceNotAvailableException(
                $"Could not fetch repository tree from GitHub (HTTP {(int)response.StatusCode}). The repository may be private or the package may lack source link metadata.");

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? root = JsonNode.Parse(json);
        JsonArray? tree = root?["tree"]?.AsArray();

        if (tree is null)
            throw new SourceNotAvailableException(
                "GitHub API returned an unexpected response format for the repository tree.");

        return tree
            .Where(n => n?["path"]?.GetValue<string>() is string p && p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!["path"]!.GetValue<string>())
            .ToList();
    }

    private async Task<Dictionary<string, string>> FetchSourceFilesAsync(
        string owner,
        string repo,
        string commitSha,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> result = new(paths.Count);
        int totalBytes = 0;

        foreach (string path in paths)
        {
            string rawUrl = $"https://raw.githubusercontent.com/{owner}/{repo}/{commitSha}/{path}";
            using HttpResponseMessage response = await _httpClient.GetAsync(rawUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
                continue; // Skip files that can't be fetched (generated, etc.)

            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            int fileBytes = System.Text.Encoding.UTF8.GetByteCount(content);

            if (totalBytes + fileBytes > MaxTotalBytes)
                throw new SourceNotAvailableException(
                    "Extension source exceeds size limits (100 files / 512 KB). Source analysis is not supported for this package.");

            totalBytes += fileBytes;
            result[path] = content;
        }

        if (result.Count == 0 && paths.Count > 0)
            throw new SourceNotAvailableException(
                $"Could not fetch any source files from the repository. The package commit SHA may be invalid or the repository may be private.");

        return result;
    }
}
