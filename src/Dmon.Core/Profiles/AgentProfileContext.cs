using Dmon.Abstractions.Profiles;

namespace Dmon.Core.Profiles;

/// <summary>
/// Singleton holder for the <see cref="AgentProfile"/> resolved at session start.
/// Groups 4-6 read <see cref="Profile"/> after the first turn populates it.
/// </summary>
/// <remarks>
/// <see cref="EnsureResolvedAsync"/> is called once per session (from
/// <c>TurnHandler.SubmitAsync</c> on the first turn, before the system prompt is built).
/// Subsequent calls are idempotent. The resolved profile is immutable for the session's
/// lifetime.
/// </remarks>
public sealed class AgentProfileContext
{
    private AgentProfile? _profile;

    /// <summary>
    /// The resolved profile for the current session.
    /// Throws if read before <see cref="EnsureResolvedAsync"/> has been called.
    /// </summary>
    public AgentProfile Profile
    {
        get => _profile ?? throw new InvalidOperationException(
            "AgentProfileContext has not been resolved yet. " +
            "EnsureResolvedAsync must be called before reading Profile.");
    }

    /// <summary>
    /// Whether the profile has been resolved for this session.
    /// </summary>
    public bool IsResolved => _profile is not null;

    /// <summary>
    /// Resolves the profile via <paramref name="resolver"/> on the first call;
    /// subsequent calls are no-ops (the resolved profile is immutable).
    /// </summary>
    /// <param name="resolver">The resolver to use for the first call.</param>
    /// <param name="requestedProfile">
    /// The per-session profile name, or <see langword="null"/> to apply configured defaults.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    public async Task EnsureResolvedAsync(
        IAgentProfileResolver resolver,
        string? requestedProfile,
        CancellationToken cancellationToken)
    {
        if (_profile is not null)
        {
            return;
        }

        AgentProfile resolved = await resolver.ResolveAsync(requestedProfile, cancellationToken)
            .ConfigureAwait(false);

        // Only the first caller wins; concurrent callers on the same turn are benign —
        // both resolve to the same profile from the same inputs, and the field is only
        // written when null.
        _profile ??= resolved;
    }
}
