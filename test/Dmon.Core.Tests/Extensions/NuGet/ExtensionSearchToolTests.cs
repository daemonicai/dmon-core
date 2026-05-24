using Dmon.Core.BuiltinTools;
using Dmon.Core.Extensions.NuGet;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Tests.Extensions.NuGet;

public sealed class ExtensionSearchToolTests
{
    // Minimal stub; search results are injected via the service stub.
    private sealed class StubSearchService : INuGetSearchService
    {
        private readonly IReadOnlyList<NuGetSearchResult> _results;

        public StubSearchService(IReadOnlyList<NuGetSearchResult> results)
        {
            _results = results;
        }

        public Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
            => Task.FromResult(_results);
    }

    [Fact]
    public void Name_IsExtensionSearchTool()
    {
        ExtensionSearchTool tool = new(new StubSearchService([]));
        Assert.Equal("Extension Search Tool", tool.Name);
    }

    [Fact]
    public void Tools_ContainsOneFunction_Named_ExtensionSearch()
    {
        ExtensionSearchTool tool = new(new StubSearchService([]));
        AIFunction function = Assert.Single(tool.Tools);
        Assert.Equal("extension.search", function.Name);
    }

    [Fact]
    public void Evaluate_AlwaysReturnsAllow()
    {
        ExtensionSearchTool tool = new(new StubSearchService([]));
        FunctionCallContent call = new("id-1", "extension.search");

        PermissionResult result = tool.Evaluate(call, new StubPermissionSettings(), null);

        Assert.Equal(PermissionResult.Allow, result);
    }

    [Fact]
    public async Task SearchAsync_NoResults_ReturnsNotFoundMessage()
    {
        ExtensionSearchTool tool = new(new StubSearchService([]));
        AIFunction function = tool.Tools.First();

        AIFunctionArguments args = new() { ["query"] = "missing-pkg" };
        object? output = await function.InvokeAsync(args, CancellationToken.None);

        string text = output?.ToString() ?? string.Empty;
        Assert.Equal("No extensions found matching \"missing-pkg\".", text);
    }

    [Fact]
    public async Task SearchAsync_OneResult_FormatsCorrectly()
    {
        IReadOnlyList<NuGetSearchResult> results =
        [
            new NuGetSearchResult
            {
                Id = "My.Extension",
                Version = "2.3.0",
                Description = "Does stuff",
                TotalDownloads = 1234,
                Stars = 42,
                PushedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ReadmeAvailable = true
            }
        ];
        ExtensionSearchTool tool = new(new StubSearchService(results));
        AIFunction function = tool.Tools.First();

        AIFunctionArguments args = new() { ["query"] = "stuff" };
        object? output = await function.InvokeAsync(args, CancellationToken.None);

        string text = output?.ToString() ?? string.Empty;
        Assert.Contains("Found 1 extension matching \"stuff\":", text);
        Assert.Contains("My.Extension v2.3.0", text);
        Assert.Contains("\"Does stuff\"", text);
        Assert.Contains("1,234 downloads", text);
        Assert.Contains("★ 42", text);
        Assert.Contains("readme_available: true", text);
    }

    [Fact]
    public async Task SearchAsync_NoGhData_ShowsDashForStars_AndStaleLabel()
    {
        IReadOnlyList<NuGetSearchResult> results =
        [
            new NuGetSearchResult
            {
                Id = "No.Gh.Pkg",
                Version = "1.0.0",
                Description = "A package",
                TotalDownloads = 500,
                Stars = null,
                PushedAt = null,
                ReadmeAvailable = false
            }
        ];
        ExtensionSearchTool tool = new(new StubSearchService(results));
        AIFunction function = tool.Tools.First();

        AIFunctionArguments args = new() { ["query"] = "pkg" };
        object? output = await function.InvokeAsync(args, CancellationToken.None);

        string text = output?.ToString() ?? string.Empty;
        Assert.Contains("★ –", text);
        Assert.Contains("stale", text);
        Assert.Contains("readme_available: false", text);
    }

    [Fact]
    public async Task SearchAsync_ActivePackage_ShowsActiveLabel()
    {
        IReadOnlyList<NuGetSearchResult> results =
        [
            new NuGetSearchResult
            {
                Id = "Fresh.Pkg",
                Version = "1.0.0",
                Description = "Fresh",
                TotalDownloads = 100,
                Stars = 5,
                PushedAt = DateTimeOffset.UtcNow.AddDays(-3),
                ReadmeAvailable = true
            }
        ];
        ExtensionSearchTool tool = new(new StubSearchService(results));
        AIFunction function = tool.Tools.First();

        AIFunctionArguments args = new() { ["query"] = "fresh" };
        object? output = await function.InvokeAsync(args, CancellationToken.None);

        string text = output?.ToString() ?? string.Empty;
        Assert.Contains("active", text);
    }

    [Fact]
    public async Task SearchAsync_ModeratePackage_ShowsModerateLabel()
    {
        IReadOnlyList<NuGetSearchResult> results =
        [
            new NuGetSearchResult
            {
                Id = "Mid.Pkg",
                Version = "1.0.0",
                Description = "Moderate",
                TotalDownloads = 100,
                Stars = 5,
                PushedAt = DateTimeOffset.UtcNow.AddDays(-90),
                ReadmeAvailable = true
            }
        ];
        ExtensionSearchTool tool = new(new StubSearchService(results));
        AIFunction function = tool.Tools.First();

        AIFunctionArguments args = new() { ["query"] = "mid" };
        object? output = await function.InvokeAsync(args, CancellationToken.None);

        string text = output?.ToString() ?? string.Empty;
        Assert.Contains("moderate", text);
    }

    // Minimal stub — the tool only reads permission-related properties not needed here.
    private sealed class StubPermissionSettings : IPermissionSettings
    {
        public PermissionSettings Settings { get; } = new();
        public Task SaveAsync(PermissionSettings updated, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
