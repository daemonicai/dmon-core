using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

public interface IEventEmitter
{
    Task EmitAsync<T>(T evt, CancellationToken cancellationToken = default) where T : Event;
}
