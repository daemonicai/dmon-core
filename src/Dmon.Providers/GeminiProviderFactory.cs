using Dmon.Core.Providers;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;

namespace Dmon.Providers;

public sealed class GeminiProviderFactory : IProviderFactory
{
    public string AdapterName => "gemini";

    public ChatClientCapabilities GetCapabilities(string modelId) => modelId.ToLowerInvariant() switch
    {
        var m when m.StartsWith("gemini-2") || m.StartsWith("gemini-1.5")
            => new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 1000000, MaxTokens = 8192 },
        _ => new() { SupportsToolCalling = false, SupportsReasoning = false }
    };

    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? string.Empty;
        GeminiClientOptions options = new()
        {
            ApiKey = apiKey ?? string.Empty,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? "gemini-2.0-flash" : modelId
        };
        if (config.BaseUrl is not null)
            options.Endpoint = new Uri(config.BaseUrl);
        ChatClientCapabilities caps = GetCapabilities(modelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(new GeminiChatClient(options), caps));
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
