using Dmon.Core.BuiltinTools;
using Dmon.Core.Extensions.NuGet;
using Dmon.Core.GitHub;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.BuiltinTools;

public sealed class ExtensionReadmeToolTests
{
    // ─── badge stripping ──────────────────────────────────────

    [Fact]
    public void StripBadges_NoBadges_ReturnsContentUnchanged()
    {
        string readme = "# My Extension\r\nSome description here.";
        string result = ExtensionReadmeTool.StripBadges(readme.Replace("\r\n", "\n"));
        Assert.StartsWith("# My Extension", result);
    }

    [Fact]
    public void StripBadges_LeadingBadges_RemovesBadgeLines()
    {
        string readme = "[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)]\n[![NuGet](https://img.shields.io/nuget/v/Foo)]\n\n# My Extension\nSome description here.";
        string result = ExtensionReadmeTool.StripBadges(readme);
        Assert.StartsWith("# My Extension", result);
        Assert.DoesNotContain("[![", result);
    }

    [Fact]
    public void StripBadges_MixedContent_StopsAtFirstNonBadgeLine()
    {
        string readme = "[![Badge1](url1)]\n\n[![Badge2](url2)]\n# Title\n[![InBody](url3)]";
        string result = ExtensionReadmeTool.StripBadges(readme);
        Assert.StartsWith("# Title", result);
        // Badge inside body is preserved
        Assert.Contains("[![InBody](url3)]", result);
    }

    [Fact]
    public void StripBadges_OnlyBadges_ReturnsEmpty()
    {
        string readme = "[![Badge](url)]\n\n[![Badge2](url2)]";
        string result = ExtensionReadmeTool.StripBadges(readme);
        Assert.True(string.IsNullOrWhiteSpace(result));
    }

    // ─── truncation ───────────────────────────────────────────

    [Fact]
    public void StripBadges_ContentOver500Chars_IsTruncatedByCallerNotStripBadges()
    {
        // StripBadges itself does NOT truncate; truncation is the caller's responsibility
        string longContent = new string('a', 600);
        string result = ExtensionReadmeTool.StripBadges(longContent);
        Assert.Equal(600, result.Length);
    }

    // ─── error paths ─────────────────────────────────────────

    [Fact]
    public async Task FetchReadme_GhUnavailable_ReturnsInstallMessage()
    {
        FakeGhCliService ghCli = new(available: false);
        FakeNuGetSearchService search = new([]);
        ExtensionReadmeTool tool = new(ghCli, search);

        string result = await InvokeFetchReadmeAsync(tool, "SomePackage");

        Assert.Contains("gh CLI", result);
        Assert.Contains("https://cli.github.com", result);
    }

    [Fact]
    public async Task FetchReadme_PackageNotFound_ReturnsNotFoundMessage()
    {
        FakeGhCliService ghCli = new(available: true);
        FakeNuGetSearchService search = new([]);
        ExtensionReadmeTool tool = new(ghCli, search);

        string result = await InvokeFetchReadmeAsync(tool, "NonExistent.Package");

        Assert.Contains("NonExistent.Package", result);
        Assert.Contains("extension.search", result);
    }

    [Fact]
    public async Task FetchReadme_NoRepositoryUrl_ReturnsNoUrlMessage()
    {
        FakeGhCliService ghCli = new(available: true);
        FakeNuGetSearchService search = new([
            MakeResult("MyPkg", repositoryUrl: null)
        ]);
        ExtensionReadmeTool tool = new(ghCli, search);

        string result = await InvokeFetchReadmeAsync(tool, "MyPkg");

        Assert.Contains("No repository URL", result);
        Assert.Contains("MyPkg", result);
    }

    [Fact]
    public async Task FetchReadme_NonGitHubUrl_ReturnsGitHubOnlyMessage()
    {
        FakeGhCliService ghCli = new(available: true);
        FakeNuGetSearchService search = new([
            MakeResult("MyPkg", repositoryUrl: "https://gitlab.com/owner/repo")
        ]);
        ExtensionReadmeTool tool = new(ghCli, search);

        string result = await InvokeFetchReadmeAsync(tool, "MyPkg");

        Assert.Contains("only supported for GitHub", result);
    }

    // ─── helpers ─────────────────────────────────────────────

    private static async Task<string> InvokeFetchReadmeAsync(ExtensionReadmeTool tool, string packageId)
    {
        AIFunction function = Assert.Single(tool.Tools);
        AIFunctionArguments args = new() { ["packageId"] = packageId };
        object? result = await function.InvokeAsync(args, CancellationToken.None);
        return result?.ToString() ?? string.Empty;
    }

    private static NuGetSearchResult MakeGitHubResult(string id, string repositoryUrl) =>
        new()
        {
            Id = id,
            Version = "1.0.0",
            RepositoryUrl = repositoryUrl,
            ReadmeAvailable = true
        };

    private static NuGetSearchResult MakeResult(string id, string? repositoryUrl) =>
        new()
        {
            Id = id,
            Version = "1.0.0",
            RepositoryUrl = repositoryUrl
        };

    // ─── fakes ───────────────────────────────────────────────

    private sealed class FakeGhCliService(bool available) : IGhCliService
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(available);
    }

    private sealed class FakeNuGetSearchService(IReadOnlyList<NuGetSearchResult> results) : INuGetSearchService
    {
        public Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
            string query,
            CancellationToken cancellationToken) =>
            Task.FromResult(results);
    }
}
