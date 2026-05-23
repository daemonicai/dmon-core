using Dmon.Protocol.Enums;
using Dmon.Protocol.Events;
using Dmon.Protocol.Models;

namespace Dmon.Core.Providers;

public sealed class ModelListHandler
{
    private readonly IProviderRegistry _registry;

    public ModelListHandler(IProviderRegistry registry)
    {
        _registry = registry;
    }

    public ModelListResultEvent Handle()
    {
        IReadOnlyList<ProviderConfig> all = _registry.GetAll();
        ProviderConfig current = _registry.GetCurrentConfig();

        List<Model> models = new(all.Count);

        foreach (ProviderConfig config in all)
        {
            models.Add(new Model
            {
                Id = config.DefaultModelId ?? string.Empty,
                Name = config.Name,
                Provider = config.Name,
                BaseUrl = config.BaseUrl,
                Reasoning = config.Capabilities.Reasoning,
                Input = [InputType.Text],
                ToolCalling = config.Capabilities.ToolCalling,
                ContextWindow = config.Capabilities.ContextWindow,
                MaxTokens = config.Capabilities.MaxTokens
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
