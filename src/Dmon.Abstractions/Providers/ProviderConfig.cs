namespace Dmon.Core.Providers;

public sealed record ProviderAuthConfig
{
    public required string Type { get; init; }
    public string? EnvVar { get; init; }
}

public sealed record ProviderConfig
{
    public required string Name { get; init; }
    public required string Adapter { get; init; }
    public string? BaseUrl { get; init; }
    public string? DefaultModelId { get; init; }
    public required ProviderAuthConfig Auth { get; init; }
}
