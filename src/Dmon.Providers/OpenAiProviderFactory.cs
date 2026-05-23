using System.ClientModel;
using Dmon.Core.Providers;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Dmon.Providers;

public sealed class OpenAiProviderFactory : IProviderFactory
{
    public string AdapterName => "openai";

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

    private sealed class CapabilitiesDecorator(IChatClient inner, ChatClientCapabilities caps) : IChatClient
    {
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientCapabilities) ? caps : inner.GetService(serviceType, serviceKey);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public void Dispose() => inner.Dispose();
    }
}
