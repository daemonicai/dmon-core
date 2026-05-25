namespace Dmon.Terminal;

internal sealed record WizardState(
    string? Adapter,
    string? ModelId,
    string? EnvVar,
    string? Scope)
{
    // Sentinel returned by a step to request Back navigation.
    public static readonly WizardState Back = new(null, null, null, "__back__");

    /// <summary>Transient: the resolved API key value. Never written to config.</summary>
    public string? ResolvedApiKey { get; init; }
}
