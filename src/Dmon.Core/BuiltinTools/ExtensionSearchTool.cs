using System.Text;
using Dmon.Core.Extensions.NuGet;
using Dmon.Abstractions.Extensions;
using Dmon.Protocol.Enums;
using Dmon.Protocol.Permissions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.BuiltinTools;

/// <summary>
/// Built-in tool that searches NuGet for dmon extensions tagged <c>dmon-extension</c>.
/// Returns a ranked shortlist of up to 5 source-available results with GitHub enrichment
/// when the gh CLI is available.
/// </summary>
internal sealed class ExtensionSearchTool : IToolExtension
{
    private readonly INuGetSearchService _searchService;
    private readonly AIFunction _function;

    public ExtensionSearchTool(INuGetSearchService searchService)
    {
        _searchService = searchService;
        _function = AIFunctionFactory.Create(
            SearchAsync,
            "extension.search",
            "Search for dmon extensions on NuGet. Returns a ranked shortlist of up to 5 source-available extensions.");
    }

    public string Name => "Extension Search Tool";
    public string Description => "Search NuGet for dmon extensions.";
    public IEnumerable<AIFunction> Tools => [_function];

    public PermissionResult Evaluate(
        FunctionCallContent call,
        IPermissionSettings project,
        IPermissionSettings? global)
        => PermissionResult.Allow;

    private async Task<string> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NuGetSearchResult> results = await _searchService.SearchAsync(query, cancellationToken);

        if (results.Count == 0)
            return $"No extensions found matching \"{query}\".";

        StringBuilder sb = new();
        sb.AppendLine($"Found {results.Count} extension{(results.Count == 1 ? "" : "s")} matching \"{query}\":");
        sb.AppendLine();

        int index = 1;
        foreach (NuGetSearchResult result in results)
        {
            string activityLabel = GetActivityLabel(result);
            string stars = result.Stars.HasValue ? result.Stars.Value.ToString() : "–";
            string downloads = result.TotalDownloads.ToString("N0");

            sb.AppendLine($"{index}. {result.Id} v{result.Version} — \"{result.Description}\"");
            sb.AppendLine($"   \U0001f4e6 {downloads} downloads  ★ {stars}  {activityLabel}");
            sb.AppendLine($"   readme_available: {(result.ReadmeAvailable ? "true" : "false")}");

            if (index < results.Count)
                sb.AppendLine();

            index++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string GetActivityLabel(NuGetSearchResult result)
    {
        if (result.PushedAt.HasValue)
        {
            double days = (DateTimeOffset.UtcNow - result.PushedAt.Value).TotalDays;
            if (days < 30) return "\U0001f7e2 active";
            if (days < 365) return "\U0001f7e1 moderate";
            return "\U0001f534 stale";
        }

        // gh unavailable or non-GitHub repo: stale label.
        return "\U0001f534 stale";
    }
}
