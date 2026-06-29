namespace Daemon.Routing;

/// <summary>
/// Configures idle-timeout behaviour for <see cref="EscalationWarmingService"/>.
/// </summary>
public sealed record EscalationWarmingOptions
{
    /// <summary>
    /// How long to wait with no session activity before stopping the escalation runtime.
    /// Defaults to 10 minutes.
    /// </summary>
    public TimeSpan IdleWindow { get; init; } = TimeSpan.FromMinutes(10);
}
