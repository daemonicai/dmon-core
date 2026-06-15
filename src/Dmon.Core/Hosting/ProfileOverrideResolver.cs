using Dmon.Abstractions.Profiles;

namespace Dmon.Hosting;

/// <summary>
/// Wraps an <see cref="IAgentProfileResolver"/> and substitutes a builder-supplied
/// profile name when the per-session <c>requestedProfile</c> argument is <see langword="null"/>.
/// This gives <see cref="DmonHostBuilder.WithProfile"/> the same precedence as a
/// config-declared <c>defaultProfile</c>, but from code rather than a file.
/// </summary>
internal sealed class ProfileOverrideResolver : IAgentProfileResolver
{
    private readonly IAgentProfileResolver _inner;
    private readonly string _profileName;

    internal ProfileOverrideResolver(IAgentProfileResolver inner, string profileName)
    {
        _inner = inner;
        _profileName = profileName;
    }

    public Task<AgentProfile> ResolveAsync(
        string? requestedProfile,
        CancellationToken cancellationToken)
    {
        // A per-session requestedProfile (non-null) keeps highest precedence.
        // When null, substitute the builder override as the default.
        string? effective = requestedProfile ?? _profileName;
        return _inner.ResolveAsync(effective, cancellationToken);
    }
}
