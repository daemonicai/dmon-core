using Dmon.Abstractions.Profiles;

namespace Dmon.Hosting;

/// <summary>
/// Wraps an <see cref="IAgentProfileResolver"/> and replaces the resolved profile's
/// <see cref="PermissionMode"/> with a builder-supplied override.
/// </summary>
internal sealed class PermissionModeOverrideResolver : IAgentProfileResolver
{
    private readonly IAgentProfileResolver _inner;
    private readonly PermissionMode _override;

    internal PermissionModeOverrideResolver(IAgentProfileResolver inner, PermissionMode @override)
    {
        _inner = inner;
        _override = @override;
    }

    public async Task<AgentProfile> ResolveAsync(
        string? requestedProfile,
        CancellationToken cancellationToken)
    {
        AgentProfile resolved = await _inner.ResolveAsync(requestedProfile, cancellationToken)
            .ConfigureAwait(false);
        return resolved with { PermissionMode = _override };
    }
}
