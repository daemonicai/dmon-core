using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;

namespace Dmon.Abstractions.Providers;

public interface IProviderFactory
{
    string AdapterName { get; }

    /// <summary>Human-readable label shown in the setup wizard.</summary>
    string DisplayName { get; }

    string DefaultModelId { get; }
    string DefaultEnvVar { get; }
    ChatClientCapabilities GetCapabilities(string modelId);
    ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(string? apiKey, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ModelInfo>>([]);

    /// <summary>
    /// Returns the next wizard step to present given the steps answered so far, or a
    /// <see cref="WizardCompletedStep"/> when setup is complete.
    /// </summary>
    ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default);
}
