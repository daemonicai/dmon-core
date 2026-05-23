using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

public interface IThinkingHandler
{
    Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken);
    Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken);
}
