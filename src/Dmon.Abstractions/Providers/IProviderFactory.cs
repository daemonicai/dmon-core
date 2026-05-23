using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Providers;

public interface IProviderFactory
{
    string AdapterName { get; }
    ChatClientCapabilities GetCapabilities(string modelId);
    ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default);
}
