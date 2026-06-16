using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;

namespace Dmon.Providers.Mtplx;

public sealed class MtplxProviderFactory : IProviderFactory
{
    private readonly MtplxOptions _options;
    private readonly MtplxRuntimeState _runtimeState;

    public MtplxProviderFactory(MtplxOptions options, MtplxRuntimeState runtimeState)
    {
        _options = options;
        _runtimeState = runtimeState;
    }

    public string AdapterName => "mtplx";
    public string DisplayName => "MTPLX";
    public string DefaultModelId => _options.ModelId ?? _runtimeState.ActiveModelId ?? string.Empty;
    public string DefaultEnvVar => "MTPLX_MODEL_ID";

    // Completed in Group 3 (mtplx-provider): chat client + probe-verified capabilities + wizard.
    public ChatClientCapabilities GetCapabilities(string modelId) =>
        throw new NotImplementedException();

    // Completed in Group 3 (mtplx-provider): chat client + probe-verified capabilities + wizard.
    public ValueTask<IChatClient> CreateAsync(
        ProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    // Completed in Group 3 (mtplx-provider): chat client + probe-verified capabilities + wizard.
    public ValueTask<WizardStep> GetNextStepAsync(
        WizardState state,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
