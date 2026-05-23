using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

public interface IAuthHandler
{
    Task LoginAsync(AuthLoginCommand cmd, CancellationToken cancellationToken);
    Task LogoutAsync(AuthLogoutCommand cmd, CancellationToken cancellationToken);
    Task StatusAsync(AuthStatusCommand cmd, CancellationToken cancellationToken);
}
