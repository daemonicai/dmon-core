using System.ClientModel;
using System.ClientModel.Primitives;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Dmon.Extensions.Omlx;

public sealed class OmlxProviderFactory : IProviderFactory
{
    private readonly OmlxConfig _config;

    public OmlxProviderFactory(OmlxConfig config)
    {
        _config = config;
    }

    public string AdapterName => "omlx";
    public string DisplayName => "oMLX";
    public string DefaultModelId => string.Empty;
    public string DefaultEnvVar => "OMLX_API_KEY";

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("oMLX provider wizard setup is not yet implemented.");

    public ChatClientCapabilities GetCapabilities(string modelId) =>
        OmlxCapabilityHeuristic.Infer(modelId);

    public ValueTask<IChatClient> CreateAsync(
        ProviderConfig config,
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? string.Empty;
        string key = apiKey ?? _config.ApiKey;
        string baseUrl = config.BaseUrl ?? _config.BaseUrl;

        OmlxAuthHandler authHandler = new(key) { InnerHandler = new HttpClientHandler() };
        HttpClient httpClient = new(authHandler);
        HttpClientPipelineTransport transport = new(httpClient);

        OpenAIClientOptions options = new()
        {
            Endpoint = new Uri(baseUrl),
            Transport = transport,
        };

        ChatClient chatClient = new(modelId, new ApiKeyCredential("none"), options);
        IChatClient client = chatClient.AsIChatClient();
        ChatClientCapabilities caps = GetCapabilities(modelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client, caps));
    }
}
