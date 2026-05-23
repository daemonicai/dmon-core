using Dmon.Core.Session;
using Dmon.Protocol.Commands;

namespace Dmon.Core.Rpc;

public interface ISessionHandler
{
    SessionMeta? CurrentSession { get; }
    Task CreateAsync(SessionCreateCommand cmd, CancellationToken cancellationToken);
    Task ForkAsync(SessionForkCommand cmd, CancellationToken cancellationToken);
    Task CloneAsync(SessionCloneCommand cmd, CancellationToken cancellationToken);
    Task LoadAsync(SessionLoadCommand cmd, CancellationToken cancellationToken);
    Task ListAsync(SessionListCommand cmd, CancellationToken cancellationToken);
    Task SetNameAsync(SessionSetNameCommand cmd, CancellationToken cancellationToken);
    Task GetStatsAsync(SessionGetStatsCommand cmd, CancellationToken cancellationToken);
    Task GetMessagesAsync(SessionGetMessagesCommand cmd, CancellationToken cancellationToken);
}
