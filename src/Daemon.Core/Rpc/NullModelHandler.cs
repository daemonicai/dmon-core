using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

/// <summary>
/// Placeholder model handler — full implementation is in a later group.
/// </summary>
internal sealed class NullModelHandler : IModelHandler
{
    public Task SetAsync(ModelSetCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("model.set not yet implemented");

    public Task CycleAsync(ModelCycleCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("model.cycle not yet implemented");

    public Task ListAsync(ModelListCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("model.list not yet implemented");
}
