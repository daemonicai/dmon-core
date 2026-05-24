using System.ClientModel;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Dmon.Providers;

public sealed class OpenAiProviderFactory : IProviderFactory
{
    public string AdapterName => "openai";
    public string DefaultModelId => "gpt-4o";
    public string DefaultEnvVar => "OPENAI_API_KEY";

    public ChatClientCapabilities GetCapabilities(string modelId) => modelId.ToLowerInvariant() switch
    {
        var m when m.StartsWith("o1") || m.StartsWith("o3")
            => new() { SupportsToolCalling = true, SupportsReasoning = true, ContextWindow = 200000, MaxTokens = 100000 },
        var m when m.StartsWith("gpt-4o") || m.StartsWith("gpt-4")
            => new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 128000, MaxTokens = 16384 },
        _ => new() { SupportsToolCalling = false, SupportsReasoning = false }
    };

    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? string.Empty;
        OpenAIClientOptions options = new();
        if (config.BaseUrl is not null)
            options.Endpoint = new Uri(config.BaseUrl);
        OpenAI.Chat.ChatClient chatClient = string.IsNullOrWhiteSpace(apiKey)
            ? new OpenAI.Chat.ChatClient(modelId, new ApiKeyCredential("none"), options)
            : new OpenAI.Chat.ChatClient(modelId, new ApiKeyCredential(apiKey), options);
        IChatClient client = chatClient.AsIChatClient();
        ChatClientCapabilities caps = GetCapabilities(modelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client, caps));
    }


}
