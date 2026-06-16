using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Dmon.Core.GitHub;

namespace Dmon.Core.Extensions.NuGet;

/// <summary>
/// Queries nuget.org for packages tagged <c>dmon-extension</c>, filters to source-available
/// packages (via nuspec inspection), enriches with GitHub signals, ranks, and caps at 5 results.
/// </summary>
internal sealed class NuGetSearchService : INuGetSearchService
{
    // Top-K candidates fetched before nuspec fan-out, to bound HTTP requests.
    private const int CandidateCap = 10;
    private const int ResultCap = 5;

    private readonly HttpClient _httpClient;
    private readonly IGhCliService _ghCliService;

    public NuGetSearchService(HttpClient httpClient, IGhCliService ghCliService)
    {
        _httpClient = httpClient;
        _ghCliService = ghCliService;
    }

    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken)
    {
        try
        {
            List<NuGetCandidate> candidates = await FetchCandidatesAsync(query, cancellationToken);
            List<NuGetSearchResult> sourceAvailable = await FilterToSourceAvailableAsync(candidates, cancellationToken);

            bool ghAvailable = await _ghCliService.IsAvailableAsync(cancellationToken);

            if (ghAvailable)
            {
                sourceAvailable = await EnrichWithGitHubAsync(sourceAvailable, cancellationToken);
            }

            // Exclude archived repos.
            List<NuGetSearchResult> eligible = sourceAvailable.Where(r => !r.Archived).ToList();

            List<NuGetSearchResult> ranked = eligible
                .Select(r => r with { Score = ComputeScore(r) })
                .OrderByDescending(r => r.Score)
                .Take(ResultCap)
                .ToList();

            return ranked;
        }
        catch
        {
            // Never throws; degradation is the contract.
            return [];
        }
    }

    private async Task<List<NuGetCandidate>> FetchCandidatesAsync(
        string query,
        CancellationToken cancellationToken)
    {
        // NuGet v3 search API: tag filter ensures dmon-extension packages only.
        string encoded = Uri.EscapeDataString(query);
        string url = $"https://azuresearch-usnc.nuget.org/query?q={encoded}+tags%3Admon-extension&take={CandidateCap}&prerelease=false&semVerLevel=2.0.0";

        using HttpResponseMessage response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return [];

        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonNode? root = JsonNode.Parse(json);
        JsonArray? data = root?["data"]?.AsArray();
        if (data is null)
            return [];

        List<NuGetCandidate> candidates = [];
        foreach (JsonNode? item in data)
        {
            if (item is null) continue;

            string? id = item["id"]?.GetValue<string>();
            if (id is null) continue;

            JsonArray? versions = item["versions"]?.AsArray();
            string? version = versions?.LastOrDefault()?["version"]?.GetValue<string>()
                ?? item["version"]?.GetValue<string>();
            if (version is null) continue;

            long downloads = item["totalDownloads"]?.GetValue<long>() ?? 0;
            string description = item["description"]?.GetValue<string>() ?? string.Empty;

            // Verify dmon-extension tag is present in the actual tags (not just the search filter).
            JsonArray? tags = item["tags"]?.AsArray();
            bool hasDmonTag = tags?.Any(t =>
                string.Equals(t?.GetValue<string>(), "dmon-extension", StringComparison.OrdinalIgnoreCase)) ?? false;
            if (!hasDmonTag) continue;

            candidates.Add(new NuGetCandidate(id, version, downloads, description));
        }

        return candidates;
    }

    private async Task<List<NuGetSearchResult>> FilterToSourceAvailableAsync(
        List<NuGetCandidate> candidates,
        CancellationToken cancellationToken)
    {
        List<NuGetSearchResult> results = [];

        foreach (NuGetCandidate candidate in candidates)
        {
            string? repoUrl = await TryGetRepositoryUrlAsync(candidate.Id, candidate.Version, cancellationToken);
            if (repoUrl is null)
                continue; // Not source-available.

            results.Add(new NuGetSearchResult
            {
                Id = candidate.Id,
                Version = candidate.Version,
                Description = candidate.Description,
                TotalDownloads = candidate.TotalDownloads,
                RepositoryUrl = repoUrl
            });
        }

        return results;
    }

    private async Task<string?> TryGetRepositoryUrlAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken)
    {
        try
        {
            // Download .nupkg from nuget.org flat container API.
            string idLower = packageId.ToLowerInvariant();
            string versionLower = version.ToLowerInvariant();
            string nupkgUrl = $"https://api.nuget.org/v3-flatcontainer/{idLower}/{versionLower}/{idLower}.{versionLower}.nupkg";

            using HttpResponseMessage nupkgResponse = await _httpClient.GetAsync(nupkgUrl, cancellationToken);
            if (!nupkgResponse.IsSuccessStatusCode)
                return null;

            await using Stream nupkgStream = await nupkgResponse.Content.ReadAsStreamAsync(cancellationToken);
            using ZipArchive archive = new(nupkgStream, ZipArchiveMode.Read, leaveOpen: false);

            ZipArchiveEntry? nuspecEntry = archive.Entries
                .FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry is null)
                return null;

            await using Stream nuspecStream = nuspecEntry.Open();
            XDocument nuspec = await XDocument.LoadAsync(nuspecStream, LoadOptions.None, cancellationToken);

            XNamespace ns = nuspec.Root?.GetDefaultNamespace() ?? XNamespace.None;
            XElement? repo = nuspec.Descendants(ns + "repository").FirstOrDefault();
            string? repoUrl = repo?.Attribute("url")?.Value;

            // A non-empty url means source-available.
            return string.IsNullOrWhiteSpace(repoUrl) ? null : repoUrl;
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<NuGetSearchResult>> EnrichWithGitHubAsync(
        List<NuGetSearchResult> results,
        CancellationToken cancellationToken)
    {
        List<NuGetSearchResult> enriched = [];

        foreach (NuGetSearchResult result in results)
        {
            if (!result.IsGitHub)
            {
                // Non-GitHub: neutral recency, no stars, readme not available.
                enriched.Add(result);
                continue;
            }

            (string owner, string repo)? ownerRepo = ExtractOwnerRepo(result.RepositoryUrl!);
            if (ownerRepo is null)
            {
                enriched.Add(result);
                continue;
            }

            GitHubRepoInfo? info = await TryGetGitHubInfoAsync(
                ownerRepo.Value.owner,
                ownerRepo.Value.repo,
                cancellationToken);

            if (info is null)
            {
                enriched.Add(result);
                continue;
            }

            // Exclude archived repos immediately.
            if (info.Archived)
            {
                enriched.Add(result with { Archived = true });
                continue;
            }

            enriched.Add(result with
            {
                Stars = info.Stars,
                PushedAt = info.PushedAt,
                ReadmeAvailable = true
            });
        }

        return enriched;
    }

    private async Task<GitHubRepoInfo?> TryGetGitHubInfoAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        try
        {
            // gh api /repos/{owner}/{repo} --jq "{stars:.stargazers_count,pushed:.pushed_at,archived:.archived}"
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gh",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("api");
            process.StartInfo.ArgumentList.Add($"/repos/{owner}/{repo}");
            process.StartInfo.ArgumentList.Add("--jq");
            process.StartInfo.ArgumentList.Add("{stars:.stargazers_count,pushed:.pushed_at,archived:.archived}");

            process.Start();

            using CancellationTokenSource timeoutCts = new(TimeSpan.FromSeconds(10));
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

            string output;
            try
            {
                output = await process.StandardOutput.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            if (process.ExitCode != 0)
                return null;

            JsonNode? json = JsonNode.Parse(output);
            if (json is null)
                return null;

            int stars = json["stars"]?.GetValue<int>() ?? 0;
            bool archived = json["archived"]?.GetValue<bool>() ?? false;
            string? pushedStr = json["pushed"]?.GetValue<string>();
            DateTimeOffset? pushedAt = DateTimeOffset.TryParse(pushedStr, out DateTimeOffset dt) ? dt : null;

            return new GitHubRepoInfo(stars, pushedAt, archived);
        }
        catch
        {
            return null;
        }
    }

    private static (string owner, string repo)? ExtractOwnerRepo(string repoUrl)
    {
        // Expects https://github.com/{owner}/{repo}[.git][/...]
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out Uri? uri))
            return null;

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
            return null;

        string owner = segments[0];
        string repo = segments[1].TrimEnd('/');
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repo = repo[..^4];

        return (owner, repo);
    }

    internal static double ComputeScore(NuGetSearchResult result)
    {
        double recency = ComputeRecencyScore(result.PushedAt);
        double downloadScore = Math.Log10(result.TotalDownloads + 1) * 0.5;
        double starScore = Math.Log10((result.Stars ?? 0) + 1) * 0.3;
        return downloadScore + starScore + recency * 0.2;
    }

    internal static double ComputeRecencyScore(DateTimeOffset? pushedAt)
    {
        if (pushedAt is null)
        {
            // No data: neutral 0.5
            return 0.5;
        }

        TimeSpan age = DateTimeOffset.UtcNow - pushedAt.Value;
        double days = age.TotalDays;

        if (days < 7) return 1.0;
        if (days < 30) return 0.8;
        if (days < 90) return 0.6;
        if (days < 365) return 0.3;
        return 0.1;
    }

    private sealed record NuGetCandidate(string Id, string Version, long TotalDownloads, string Description);
    private sealed record GitHubRepoInfo(int Stars, DateTimeOffset? PushedAt, bool Archived);
}
