using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

public interface IAuthHandler
{
    Task LoginAsync(AuthLoginCommand cmd, CancellationToken cancellationToken);
    Task LogoutAsync(AuthLogoutCommand cmd, CancellationToken cancellationToken);
    Task StatusAsync(AuthStatusCommand cmd, CancellationToken cancellationToken);
}
