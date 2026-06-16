using System.ClientModel;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

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

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("MTPLX provider is attach-based and zero-config; no wizard setup required.");

    public ChatClientCapabilities GetCapabilities(string modelId) =>
        new()
        {
            SupportsToolCalling = _runtimeState.ToolCallingVerified ?? false,
        };

    public ValueTask<IChatClient> CreateAsync(
        ProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? _options.ModelId ?? _runtimeState.ActiveModelId ?? string.Empty;
        string baseUrl = config.BaseUrl
            ?? (string.IsNullOrEmpty(_runtimeState.BaseUrl)
                ? $"http://{_options.Host}:{_options.Port}/v1"
                : _runtimeState.BaseUrl);

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(baseUrl),
        };

        ChatClient chatClient = new(modelId, new ApiKeyCredential(apiKey ?? "none"), options);
        IChatClient client = chatClient.AsIChatClient();
        ChatClientCapabilities caps = GetCapabilities(modelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client, caps));
    }
}
