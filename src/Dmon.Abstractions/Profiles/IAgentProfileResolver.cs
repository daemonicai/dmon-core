namespace Dmon.Abstractions.Profiles;

/// <summary>
/// Resolves the <see cref="AgentProfile"/> to use for an incoming session.
/// Called exactly once per session; the result is immutable for the session's lifetime.
/// </summary>
public interface IAgentProfileResolver
{
    /// <summary>
    /// Returns the resolved <see cref="AgentProfile"/> for the session.
    /// </summary>
    /// <param name="requestedProfile">
    /// The per-session profile name supplied by the caller, or <see langword="null"/>
    /// to fall back to configured defaults.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation of the resolve operation.</param>
    Task<AgentProfile> ResolveAsync(string? requestedProfile, CancellationToken cancellationToken);
}
