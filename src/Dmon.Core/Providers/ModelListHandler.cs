using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;

namespace Dmon.Core.Providers;

public sealed class ModelListHandler
{
    private readonly IProviderRegistry _registry;
    private readonly IReadOnlyDictionary<string, IProviderFactory> _factories;

    public ModelListHandler(IProviderRegistry registry, IEnumerable<IProviderFactory> factories)
    {
        _registry = registry;
        _factories = factories.ToDictionary(f => f.AdapterName, StringComparer.OrdinalIgnoreCase);
    }

    public ModelListResultEvent Handle()
    {
        IReadOnlyList<ProviderConfig> all = _registry.GetAll();
        ProviderConfig current = _registry.GetCurrentConfig();

        List<Model> models = new(all.Count);

        foreach (ProviderConfig config in all)
        {
            ChatClientCapabilities caps = _factories.TryGetValue(config.Adapter, out IProviderFactory? factory)
                ? factory.GetCapabilities(config.DefaultModelId ?? string.Empty)
                : new ChatClientCapabilities();

            models.Add(new Model
            {
                Id = config.DefaultModelId ?? string.Empty,
                Name = config.Name,
                Provider = config.Name,
                BaseUrl = config.BaseUrl,
                Reasoning = caps.SupportsReasoning,
                Input = [InputType.Text],
                ToolCalling = caps.SupportsToolCalling,
                ContextWindow = caps.ContextWindow,
                MaxTokens = caps.MaxTokens
            });
        }

        return new ModelListResultEvent
        {
            Models = models,
            ActiveProvider = current.Name,
            ActiveModelId = current.DefaultModelId ?? string.Empty
        };
    }
}
