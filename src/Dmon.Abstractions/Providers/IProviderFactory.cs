using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Providers;

public interface IProviderFactory
{
    string AdapterName { get; }
    string DefaultModelId { get; }
    string DefaultEnvVar { get; }
    ChatClientCapabilities GetCapabilities(string modelId);
    ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ModelInfo>>([]);
}
