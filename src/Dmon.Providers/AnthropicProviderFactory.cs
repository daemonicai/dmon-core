using System.Text.Json;
using Anthropic.SDK;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;

namespace Dmon.Providers;

public sealed class AnthropicProviderFactory : IProviderFactory
{
    public string AdapterName => "anthropic";
    public string DefaultModelId => "claude-sonnet-4-6";
    public string DefaultEnvVar => "ANTHROPIC_API_KEY";

    private static readonly IReadOnlyList<ModelInfo> FallbackModels =
    [
        new ModelInfo { Id = "claude-opus-4-7",           Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = true,  ContextWindow = 200000, MaxTokens = 32000 } },
        new ModelInfo { Id = "claude-sonnet-4-6",         Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = true,  ContextWindow = 200000, MaxTokens = 32000 } },
        new ModelInfo { Id = "claude-haiku-4-5-20251001", Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 200000, MaxTokens = 8192  } },
    ];

    public ChatClientCapabilities GetCapabilities(string modelId) => modelId.ToLowerInvariant() switch
    {
        var m when m.StartsWith("claude-opus-4") || m.StartsWith("claude-sonnet-4")
            => new() { SupportsToolCalling = true, SupportsReasoning = true, ContextWindow = 200000, MaxTokens = 32000 },
        var m when m.StartsWith("claude-3") || m.StartsWith("claude-haiku-4")
            => new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 200000, MaxTokens = 8192 },
        _ => new() { SupportsToolCalling = false, SupportsReasoning = false }
    };

    public async ValueTask<IReadOnlyList<ModelInfo>> GetAvailableModelsAsync(
        string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return FallbackModels;

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            using HttpClient http = new();
            http.DefaultRequestHeaders.Add("x-api-key", apiKey);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

            using HttpResponseMessage response = await http.GetAsync(
                "https://api.anthropic.com/v1/models",
                timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return FallbackModels;

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out JsonElement dataElement))
                return FallbackModels;

            List<ModelInfo> models = [];
            foreach (JsonElement model in dataElement.EnumerateArray())
            {
                if (!model.TryGetProperty("id", out JsonElement idElement))
                    continue;

                string id = idElement.GetString() ?? string.Empty;
                if (string.IsNullOrEmpty(id))
                    continue;

                models.Add(new ModelInfo { Id = id, Capabilities = GetCapabilities(id) });
            }

            return models.Count > 0 ? models : FallbackModels;
        }
        catch
        {
            return FallbackModels;
        }
    }

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
