using Dmon.Abstractions.Providers;
using Dmon.Abstractions.Wizard;
using Dmon.Protocol.Wizard;
using Microsoft.Extensions.AI;
using OllamaSharp;
using DmonModelInfo = Dmon.Abstractions.Providers.ModelInfo;

namespace Dmon.Providers.Ollama;

public sealed class OllamaProviderFactory : IProviderFactory
{
    public string AdapterName => "ollama";
    public string DisplayName => "Ollama";
    public string DefaultModelId => "llama3.2";
    public string DefaultEnvVar => "OLLAMA_HOST";

    public ChatClientCapabilities GetCapabilities(string modelId)
    {
        string m = modelId.ToLowerInvariant();

        if (m.Contains("embed") || m.Contains("rerank"))
            return new() { SupportsToolCalling = false, SupportsReasoning = false, ContextWindow = 8192, MaxTokens = 4096 };

        bool reasoning = m.StartsWith("qwen3") || m.Contains("reason") || m.Contains("thinking") || m.Contains("r1");
        bool vision = m.Contains("vlm") || m.Contains("vision") || m.Contains("-vl-") || m.EndsWith(":vl");
        bool toolCalling = reasoning || vision
            || m.Contains("-instruct") || m.Contains("-chat") || m.Contains("-it-") || m.EndsWith("-it")
            || m.EndsWith(":instruct") || m.EndsWith(":chat") || m.EndsWith(":it");

        return new()
        {
            SupportsToolCalling = toolCalling,
            SupportsReasoning = reasoning,
            ContextWindow = 8192,
            MaxTokens = 4096,
        };
    }

    public async ValueTask<IReadOnlyList<DmonModelInfo>> GetAvailableModelsAsync(
        string? apiKey, CancellationToken cancellationToken = default)
    {
        Uri uri = NormalizeOllamaBaseUri(apiKey);

        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            OllamaApiClient client = new(uri);
            IEnumerable<OllamaSharp.Models.Model> remoteModels =
                await client.ListLocalModelsAsync(timeoutCts.Token).ConfigureAwait(false);

            List<DmonModelInfo> models = [];
            foreach (OllamaSharp.Models.Model model in remoteModels)
            {
                if (string.IsNullOrEmpty(model.Name))
                    continue;
                models.Add(new DmonModelInfo { Id = model.Name, Capabilities = GetCapabilities(model.Name) });
            }

            return models;
        }
        catch
        {
            return [];
        }
    }

    public async ValueTask<WizardStep> GetNextStepAsync(
        WizardState state, CancellationToken cancellationToken = default)
    {
        ChooseOneStep? deploymentStep = state.Steps
            .OfType<ChooseOneStep>()
            .FirstOrDefault(s => s.Id == "deployment");

        if (deploymentStep is null || !deploymentStep.IsAnswered)
        {
            return new ChooseOneStep
            {
                Id = "deployment",
                Prompt = "Where is Ollama running?",
                Options =
                [
                    new WizardOption("Local (localhost)", "local"),
                    new WizardOption("Cloud (ollama.com)", "cloud"),
                ],
            };
        }

        TextInputStep? baseUrlStep = state.Steps
            .OfType<TextInputStep>()
            .FirstOrDefault(s => s.Id == "base-url");

        if (baseUrlStep is null || !baseUrlStep.IsAnswered)
        {
            string deploymentValue = deploymentStep.Options[deploymentStep.SelectedIndex!.Value].Value;
            string deploymentDefault = deploymentValue == "cloud"
                ? "https://ollama.com/api"
                : "http://localhost:11434/api";

            string? envValue = Environment.GetEnvironmentVariable(DefaultEnvVar);
            bool hasEnvVar = !string.IsNullOrWhiteSpace(envValue);

            return new TextInputStep
            {
                Id = "base-url",
                Prompt = "Ollama base URL",
                Secret = false,
                Required = !hasEnvVar,
                Default = hasEnvVar ? envValue : deploymentDefault,
            };
        }

        ChooseOneStep? modelStep = state.Steps
            .OfType<ChooseOneStep>()
            .FirstOrDefault(s => s.Id == "model");

        if (modelStep is null || !modelStep.IsAnswered)
        {
            string baseUrl = baseUrlStep.Value ?? baseUrlStep.Default ?? "http://localhost:11434";
            IReadOnlyList<DmonModelInfo> models = await GetAvailableModelsAsync(baseUrl, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<WizardOption> options = models.Count > 0
                ? models.Select(m => new WizardOption(m.Id, m.Id)).ToList()
                : (IReadOnlyList<WizardOption>)[new WizardOption("(Ollama is not reachable — start Ollama and re-run setup)", string.Empty)];

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
            Message = $"✓ Ollama configured with model {selectedModelId}.",
        };
    }

    public ValueTask<IChatClient> CreateAsync(
        ProviderConfig config, string? apiKey, CancellationToken cancellationToken = default)
    {
        Uri uri = NormalizeOllamaBaseUri(config.BaseUrl);
        OllamaApiClient client = new(uri);
        ChatClientCapabilities caps = GetCapabilities(config.DefaultModelId ?? DefaultModelId);
        return ValueTask.FromResult<IChatClient>(new CapabilitiesDecorator(client, caps));
    }

    private static Uri NormalizeOllamaBaseUri(string? url, string fallback = "http://localhost:11434")
    {
        string raw = string.IsNullOrWhiteSpace(url) ? fallback : url.TrimEnd('/');
        if (raw.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^4]; // strip trailing "/api" — OllamaSharp appends it itself
        return Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed) && parsed is not null
            ? parsed
            : new Uri(fallback);
    }
}
