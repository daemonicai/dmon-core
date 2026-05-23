using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

/// <summary>
/// Placeholder thinking handler — full implementation is in a later group.
/// </summary>
internal sealed class NullThinkingHandler : IThinkingHandler
{
    public Task SetAsync(ThinkingSetCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("thinking.set not yet implemented");

    public Task CycleAsync(ThinkingCycleCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("thinking.cycle not yet implemented");
}
