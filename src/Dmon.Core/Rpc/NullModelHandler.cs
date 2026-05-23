using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

/// <summary>
/// Placeholder model handler — full implementation is in a later group.
/// </summary>
internal sealed class NullModelHandler : IModelHandler
{
    private readonly IEventEmitter _emitter;

    public NullModelHandler(IEventEmitter emitter)
    {
        _emitter = emitter;
    }

    public Task SetAsync(ModelSetCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("model.set not yet implemented");

    public Task CycleAsync(ModelCycleCommand cmd, CancellationToken cancellationToken) =>
        throw new NotImplementedException("model.cycle not yet implemented");

    public async Task ListAsync(ModelListCommand cmd, CancellationToken cancellationToken)
    {
        await _emitter.EmitAsync(new ResponseEvent
        {
            RequestId = cmd.Id,
            Command = "model.list",
            Success = true,
            Data = Array.Empty<object>()
        }, cancellationToken).ConfigureAwait(false);
    }
}
