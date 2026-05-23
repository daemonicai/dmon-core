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

    // Must only be called strictly between turns — never during an active streaming call.
    ProviderSwitchResult? CommitPendingSwitch();

    bool CurrentSupportsToolCalling { get; }
    bool CurrentSupportsReasoning { get; }
}
