using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Providers;

public interface IProviderRegistry
{
    ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default);
    ProviderConfig GetCurrentConfig();
    IReadOnlyList<ProviderConfig> GetAll();
    void SetProvider(string name);
    void SetModel(string modelId);
    void CycleProvider();

    /// <summary>
    /// Registers a provider extension at runtime. Synthesises a ProviderConfig
    /// from the extension and adds it to the set of available providers.
    /// The provider is immediately selectable via SetProvider(extension.ProviderName).
    /// </summary>
    Task RegisterExtensionAsync(IProviderExtension extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a provider that was configured at runtime via <c>provider.configure</c>.
    /// The provider is immediately available; no restart required.
    /// </summary>
    void AddDynamicProvider(ProviderConfig config);

    string? GetCurrentModelId();

    // Must only be called strictly between turns — never during an active streaming call.
    ProviderSwitchResult? CommitPendingSwitch();

    bool CurrentSupportsToolCalling { get; }
    bool CurrentSupportsReasoning { get; }
}
