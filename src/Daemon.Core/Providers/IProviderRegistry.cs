using Microsoft.Extensions.AI;
using Daemon.Protocol.Events;

namespace Daemon.Core.Providers;

public interface IProviderRegistry
{
    ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default);
    ProviderConfig GetCurrentConfig();
    IReadOnlyList<ProviderConfig> GetAll();
    void SetProvider(string name, string? modelId = null);
    void CycleProvider();

    /// <summary>
    /// Commits any pending provider switch and disposes the previous <see cref="IChatClient"/> immediately.
    /// Must only be called strictly between turns — never during an active streaming call.
    /// </summary>
    ProviderSwitchedEvent? CommitPendingSwitch();

    bool CurrentSupportsToolCalling { get; }
    bool CurrentSupportsReasoning { get; }
}
