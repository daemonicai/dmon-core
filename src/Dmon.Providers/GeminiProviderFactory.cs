using System.Text.Json;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;

namespace Dmon.Providers;

public sealed class GeminiProviderFactory : IProviderFactory
{
    public string AdapterName => "gemini";
    public string DisplayName => "Google Gemini";
    public string DefaultModelId => "gemini-2.5-pro";
    public string DefaultEnvVar => "GEMINI_API_KEY";

    public ValueTask<WizardStep> GetNextStepAsync(WizardState state, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Gemini provider wizard setup is not yet implemented.");

    private static readonly IReadOnlyList<ModelInfo> FallbackModels =
    [
        new ModelInfo { Id = "gemini-2.5-pro",   Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 1000000, MaxTokens = 8192 } },
        new ModelInfo { Id = "gemini-2.5-flash", Capabilities = new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 1000000, MaxTokens = 8192 } },
    ];

    public ChatClientCapabilities GetCapabilities(string modelId) => modelId.ToLowerInvariant() switch
    {
        var m when m.StartsWith("gemini-2") || m.StartsWith("gemini-1.5")
            => new() { SupportsToolCalling = true, SupportsReasoning = false, ContextWindow = 1000000, MaxTokens = 8192 },
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
            using HttpResponseMessage response = await http.GetAsync(
                $"https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}",
                timeoutCts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return FallbackModels;

            string json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out JsonElement modelsElement))
                return FallbackModels;

            List<ModelInfo> models = [];
            foreach (JsonElement model in modelsElement.EnumerateArray())
            {
                if (!model.TryGetProperty("name", out JsonElement nameElement))
                    continue;

                string name = nameElement.GetString() ?? string.Empty;
                if (!name.StartsWith("models/gemini"))
                    continue;

                string id = name["models/".Length..];
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
}
