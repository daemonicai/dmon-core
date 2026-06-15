using Dmon.Abstractions.Providers;
using Dmon.Protocol.Commands;
using Dmon.Protocol.Events;

namespace Dmon.Core.Providers;

public sealed class ModelModelsHandler
{
    private readonly IProviderRegistry _registry;
    private readonly IReadOnlyDictionary<string, IProviderFactory> _factories;
    private readonly ICredentialResolver _credentials;

    public ModelModelsHandler(
        IProviderRegistry registry,
        IEnumerable<IProviderFactory> factories,
        ICredentialResolver credentials)
    {
        _registry = registry;
        _factories = factories.ToDictionary(f => f.AdapterName, StringComparer.OrdinalIgnoreCase);
        _credentials = credentials;
    }

    public async Task<ModelModelsResultEvent> HandleAsync(ModelModelsCommand cmd, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderConfig> all = _registry.GetAll();
        ProviderConfig? config = all.FirstOrDefault(
            c => string.Equals(c.Name, cmd.Provider, StringComparison.OrdinalIgnoreCase));

        if (config is null || !_factories.TryGetValue(config.Adapter, out IProviderFactory? factory))
        {
            return new ModelModelsResultEvent
            {
                CommandId = cmd.Id,
                Provider = cmd.Provider,
                Models = [],
                ActiveModelId = _registry.GetCurrentModelId()
            };
        }

        string? apiKey = null;
        if (!string.Equals(config.Auth.Type, "none", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = await _credentials.ResolveAsync(config.Name, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> modelIds;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            IReadOnlyList<ModelInfo> models = await factory.GetAvailableModelsAsync(apiKey, config.BaseUrl, timeout.Token).ConfigureAwait(false);
            modelIds = models.Select(m => m.Id).ToList();
        }
        catch (OperationCanceledException)
        {
            modelIds = [];
        }
        catch (Exception)
        {
            modelIds = [];
        }

        return new ModelModelsResultEvent
        {
            CommandId = cmd.Id,
            Provider = cmd.Provider,
            Models = modelIds,
            ActiveModelId = _registry.GetCurrentModelId()
        };
    }
}
