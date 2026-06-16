using System.ClientModel;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Dmon.Providers.LlamaCpp;

public sealed class LlamaCppProviderFactory : IProviderFactory
{
    private readonly LlamaCppOptions _options;
    private readonly LlamaCppRuntimeState _runtimeState;

    public LlamaCppProviderFactory(LlamaCppOptions options, LlamaCppRuntimeState runtimeState)
    {
        _options = options;
        _runtimeState = runtimeState;
    }

    public string AdapterName => "llamacpp";
    public string DisplayName => "llama.cpp";
    public string DefaultModelId => _options.ModelId;
    public string DefaultEnvVar => "LLAMA_MODEL_ID";

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("llama.cpp provider is PATH-based and zero-config; no wizard setup required.");

    public ChatClientCapabilities GetCapabilities(string modelId) =>
        new()
        {
            SupportsToolCalling = _runtimeState.ToolCallingVerified ?? false,
            ContextWindow = _options.ContextSize ?? 0,
        };

    public ValueTask<IChatClient> CreateAsync(
        ProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? _options.ModelId;
        string baseUrl = config.BaseUrl ?? _runtimeState.BaseUrl;

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
