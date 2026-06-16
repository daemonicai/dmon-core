using Dmon.Protocol.Commands;
using Dmon.Protocol.Enums;

namespace Dmon.Core.Rpc;

public interface IThinkingHandler
{
    ThinkingLevel CurrentLevel { get; }
    Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken);
    Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken);
}
