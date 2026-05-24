namespace Dmon.Core.Extensions.NuGet;

/// <summary>
/// Represents a single NuGet package candidate after source-availability and GitHub enrichment.
/// </summary>
internal sealed record NuGetSearchResult
{
    public required string Id { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = string.Empty;
    public long TotalDownloads { get; init; }

    // Source-availability (nuspec <repository url="...">)
    public string? RepositoryUrl { get; init; }

    // GitHub enrichment (null if gh unavailable or non-GitHub repo)
    public int? Stars { get; init; }
    public DateTimeOffset? PushedAt { get; init; }
    public bool Archived { get; init; }

    public bool IsGitHub =>
        RepositoryUrl is not null &&
        RepositoryUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase);

    public bool ReadmeAvailable { get; init; }

    public double Score { get; init; }
}
