using System.Diagnostics;
using Dmon.Core.Extensions.NuGet;
using Dmon.Core.GitHub;
using Dmon.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.BuiltinTools;

internal sealed class ExtensionReadmeTool : IDmonExtension
{
    private const int ExcerptLength = 500;

    private readonly IGhCliService _ghCliService;
    private readonly INuGetSearchService _searchService;
    private readonly AIFunction _function;

    public ExtensionReadmeTool(IGhCliService ghCliService, INuGetSearchService searchService)
    {
        _ghCliService = ghCliService;
        _searchService = searchService;
        _function = AIFunctionFactory.Create(
            FetchReadmeAsync,
            "extension.readme",
            "Fetch the README excerpt for a dmon extension. Use the package ID returned by extension.search.");
    }

    public string Name => "Extension Readme Tool";
    public string Description => "Fetch the README excerpt for a dmon extension.";
    public IEnumerable<AIFunction> Tools => [_function];

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Allow;

    private async Task<string> FetchReadmeAsync(
        [System.ComponentModel.Description("The package ID of the extension, as returned by extension.search.")]
        string packageId,
        CancellationToken cancellationToken = default)
    {
        bool ghAvailable = await _ghCliService.IsAvailableAsync(cancellationToken);
        if (!ghAvailable)
            return "README fetch requires the gh CLI. Install gh (https://cli.github.com) and run 'gh auth login' to enable this feature.";

        IReadOnlyList<NuGetSearchResult> results = await _searchService.SearchAsync(packageId, cancellationToken);
        NuGetSearchResult? match = results.FirstOrDefault(r =>
            string.Equals(r.Id, packageId, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return $"No extension found with ID \"{packageId}\". Try extension.search to find the correct package ID.";

        if (string.IsNullOrWhiteSpace(match.RepositoryUrl))
            return $"No repository URL available for {packageId}. README fetch is not supported for this extension.";

        if (!match.IsGitHub)
            return "README fetch is only supported for GitHub-hosted extensions.";

        (string owner, string repo)? ownerRepo = ExtractOwnerRepo(match.RepositoryUrl);
        if (ownerRepo is null)
            return $"Could not parse GitHub repository URL for {packageId}.";

        string? content = await FetchReadmeContentAsync(ownerRepo.Value.owner, ownerRepo.Value.repo, cancellationToken);
        if (content is null)
            return $"Could not fetch README for {packageId}: gh command failed or README not found.";

        string cleaned = StripBadges(content);
        return cleaned.Length <= ExcerptLength
            ? cleaned
            : cleaned[..ExcerptLength];
    }

    private static async Task<string?> FetchReadmeContentAsync(
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        try
        {
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
            process.StartInfo.ArgumentList.Add($"/repos/{owner}/{repo}/readme");
            process.StartInfo.ArgumentList.Add("--jq");
            process.StartInfo.ArgumentList.Add(".content");

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

            // .content is a base64-encoded string (with possible newlines inside the value)
            string base64 = output.Trim().Replace("\n", "").Replace("\r", "");
            if (string.IsNullOrEmpty(base64))
                return null;

            byte[] bytes = Convert.FromBase64String(base64);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    internal static string StripBadges(string readme)
    {
        string[] lines = readme.Split('\n');
        int start = lines.Length;
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[![", StringComparison.Ordinal) || trimmed.Length == 0)
                continue;
            start = i;
            break;
        }

        return string.Join('\n', lines[start..]).TrimStart();
    }

    private static (string owner, string repo)? ExtractOwnerRepo(string repoUrl)
    {
        if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out Uri? uri))
            return null;

        string[] segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2)
            return null;

        string owner = segments[0];
        string repoName = segments[1].TrimEnd('/');
        if (repoName.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoName = repoName[..^4];

        return (owner, repoName);
    }
}
