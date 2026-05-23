using Daemon.Protocol.Commands;

namespace Daemon.Core.Rpc;

public interface IModelHandler
{
    Task SetAsync(ModelSetCommand cmd, CancellationToken cancellationToken);
    Task CycleAsync(ModelCycleCommand cmd, CancellationToken cancellationToken);
    Task ListAsync(ModelListCommand cmd, CancellationToken cancellationToken);
}
