namespace Dmon.Core.Extensions.Security;

public sealed record SourceFetchResult
{
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string CommitSha { get; init; }

    /// <summary>Source files: path → content. Capped at MaxFiles / MaxTotalBytes.</summary>
    public required IReadOnlyDictionary<string, string> SourceFiles { get; init; }
}
