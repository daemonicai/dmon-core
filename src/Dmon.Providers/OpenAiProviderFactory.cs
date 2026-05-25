using System.ClientModel;
using System.Text.Json;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Dmon.Providers;

public sealed class OpenAiProviderFactory : IProviderFactory
{
    public string AdapterName => "openai";
    public string DefaultModelId => "gpt-4o";
    public string DefaultEnvVar => "OPENAI_API_KEY";

    private static readonly IReadOnlyList<ModelInfo> FallbackModels =
    [
        new ModelInfo { Id = "gpt-4o",      Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 128000, MaxTokens = 16384  } },
        new ModelInfo { Id = "gpt-4o-mini", Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 128000, MaxTokens = 16384  } },
        new ModelInfo { Id = "o3",          Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = true,  ContextWindow = 200000, MaxTokens = 100000 } },
    ];

    public ChatClientCapabilities GetCapabilities(string modelId) => modelId.ToLowerInvariant() switch
    {
        var m when m.StartsWith("o1") || m.StartsWith("o3")
            => new() { SupportsToolCalling = true, SupportsReasoning = true, ContextWindow = 200000, MaxTokens = 100000 },
        var m when m.StartsWith("gpt-4o") || m.StartsWith("gpt-4")
            => new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 128000, MaxTokens = 16384 },
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
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            using HttpResponseMessage response = await http.GetAsync(
                "https://api.openai.com/v1/models",
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

                if (!id.StartsWith("gpt-") && !(id.StartsWith("o") && id.Length > 1 && char.IsDigit(id[1])))
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
