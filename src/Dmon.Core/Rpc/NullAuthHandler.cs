using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

/// <summary>
/// Placeholder auth handler — full implementation is in a later group.
/// </summary>
internal sealed class NullAuthHandler : IAuthHandler
{
    public Task LoginAsync(AuthLoginCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("auth.login not yet implemented");

    public Task LogoutAsync(AuthLogoutCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("auth.logout not yet implemented");

    public Task StatusAsync(AuthStatusCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("auth.status not yet implemented");
}
