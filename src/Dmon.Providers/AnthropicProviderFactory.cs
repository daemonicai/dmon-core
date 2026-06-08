using System.Text.Json;
using Anthropic;
using Anthropic.Core;
using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;

namespace Dmon.Providers;

public sealed class AnthropicProviderFactory : IProviderFactory
{
    public string AdapterName => "anthropic";
    public string DisplayName => "Anthropic";
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
        string? apiKey, string? baseUrl = null, CancellationToken cancellationToken = default)
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

    public async ValueTask<WizardStep> GetNextStepAsync(
        WizardState state, CancellationToken cancellationToken = default)
    {
        TextInputStep? apiKeyStep = state.Steps
            .OfType<TextInputStep>()
            .FirstOrDefault(s => s.Id == "api-key");

        if (apiKeyStep is null || !apiKeyStep.IsAnswered)
        {
            string? envVarValue = Environment.GetEnvironmentVariable(DefaultEnvVar);
            bool hasEnvVar = !string.IsNullOrWhiteSpace(envVarValue);

            return new TextInputStep
            {
                Id = "api-key",
                Prompt = hasEnvVar ? $"API key (or use ${DefaultEnvVar})" : "API key",
                Secret = true,
                Required = !hasEnvVar,
                Default = hasEnvVar ? envVarValue : null,
            };
        }

        ChooseOneStep? modelStep = state.Steps
            .OfType<ChooseOneStep>()
            .FirstOrDefault(s => s.Id == "model");

        if (modelStep is null || !modelStep.IsAnswered)
        {
            IReadOnlyList<ModelInfo> models = await GetAvailableModelsAsync(
                apiKeyStep.Value, cancellationToken: cancellationToken).ConfigureAwait(false);

            IReadOnlyList<WizardOption> options = models.Count > 0
                ? models.Select(m => new WizardOption(m.Id, m.Id)).ToList()
                : (IReadOnlyList<WizardOption>)[new WizardOption("(no models found)", string.Empty)];

            return new ChooseOneStep
            {
                Id = "model",
                Prompt = "Select a model",
                Options = options,
            };
        }

        string selectedModelId = modelStep.Options[modelStep.SelectedIndex!.Value].Value;

        return new WizardCompletedStep
        {
            Id = "completed",
            Prompt = string.Empty,
            Message = $"✓ {DisplayName} configured with model {selectedModelId}.",
        };
    }

    public ValueTask<IChatClient> CreateAsync(ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
    {
        string modelId = config.DefaultModelId ?? string.Empty;
        ClientOptions options = new();
        if (!string.IsNullOrWhiteSpace(apiKey))
            options.ApiKey = apiKey;
        if (config.BaseUrl is not null)
            options.BaseUrl = config.BaseUrl;
        AnthropicClient client = new(options);
        ChatClientCapabilities caps = GetCapabilities(modelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client.AsIChatClient(modelId), caps));
    }
}
