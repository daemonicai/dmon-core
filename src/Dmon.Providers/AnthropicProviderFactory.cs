using Anthropic.SDK;
using Dmon.Core.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers;

public sealed class AnthropicProviderFactory : IProviderFactory
{
    public string AdapterName => "anthropic";

    public ChatClientCapabilities GetCapabilities(string modelId) => modelId.ToLowerInvariant() switch
    {
        var m when m.StartsWith("claude-opus-4") || m.StartsWith("claude-sonnet-4")
            => new() { SupportsToolCalling = true, SupportsReasoning = true, ContextWindow = 200000, MaxTokens = 32000 },
        var m when m.StartsWith("claude-3") || m.StartsWith("claude-haiku-4")
            => new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 200000, MaxTokens = 8192 },
        _ => new() { SupportsToolCalling = false, SupportsReasoning = false }
    };

    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? string.Empty;
        AnthropicClient client = string.IsNullOrWhiteSpace(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient(apiKey);
        if (config.BaseUrl is not null)
            client.ApiUrlFormat = $"{config.BaseUrl.TrimEnd('/')}/{{0}}/{{1}}";
        ChatClientCapabilities caps = GetCapabilities(modelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client.Messages, caps));
    }


}
