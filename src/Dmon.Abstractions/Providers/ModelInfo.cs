namespace Dmon.Abstractions.Providers;

/// <summary>
/// Describes a model available from a provider.
/// </summary>
public sealed record ModelInfo
{
    /// <summary>The model identifier as returned by the provider (e.g. "llama3.2").</summary>
    public required string Id { get; init; }

    /// <summary>Capabilities inferred or declared for this model.</summary>
    public required ChatClientCapabilities Capabilities { get; init; }
}
