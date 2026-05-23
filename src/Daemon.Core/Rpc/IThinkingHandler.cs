using Daemon.Protocol.Commands;
using Daemon.Protocol.Enums;

namespace Daemon.Core.Rpc;

public interface IThinkingHandler
{
    ThinkingLevel CurrentLevel { get; }
    Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken);
    Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken);
}
