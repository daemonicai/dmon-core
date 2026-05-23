using Dmon.Core.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

/// <summary>
/// Placeholder model handler — full implementation is in a later group.
/// </summary>
internal sealed class NullModelHandler : IModelHandler
{
    private readonly IProviderRegistry _providers;
    private readonly IEventEmitter _emitter;

    public NullModelHandler(IProviderRegistry providers, IEventEmitter emitter)
    {
        _providers = providers;
        _emitter = emitter;
    }

    public Task SetAsync(ModelSetCommand cmd, CancellationToken cancellationToken)
    {
        _providers.SetProvider(cmd.Provider);
        _providers.SetModel(cmd.ModelId);
        return Task.CompletedTask;
    }

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
