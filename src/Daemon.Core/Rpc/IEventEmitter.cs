using Daemon.Protocol.Events;

namespace Daemon.Core.Rpc;

public interface IEventEmitter
{
    Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event;
}
