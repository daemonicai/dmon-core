using Dmon.Core.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Rpc;

internal sealed class NullModelHandler : IModelHandler
{
    private readonly IProviderRegistry _providers;
    private readonly IEventEmitter _emitter;
    private readonly ModelListHandler _listHandler;
    private readonly ModelModelsHandler _modelsHandler;

    public NullModelHandler(
        IProviderRegistry providers,
        IEventEmitter emitter,
        ModelListHandler listHandler,
        ModelModelsHandler modelsHandler)
    {
        _providers = providers;
        _emitter = emitter;
        _listHandler = listHandler;
        _modelsHandler = modelsHandler;
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
        ModelListResultEvent result = _listHandler.Handle(cmd.Id);
        await _emitter.EmitAsync(result, cancellationToken).ConfigureAwait(false);
    }

    public async Task ModelsAsync(ModelModelsCommand cmd, CancellationToken cancellationToken)
    {
        ModelModelsResultEvent result = await _modelsHandler.HandleAsync(cmd, cancellationToken).ConfigureAwait(false);
        await _emitter.EmitAsync(result, cancellationToken).ConfigureAwait(false);
    }
}
