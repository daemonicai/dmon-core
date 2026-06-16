using Dmon.Abstractions.Providers;
using OllamaSharp;
using DmonModelInfo = Dmon.Abstractions.Providers.ModelInfo;

namespace Dmon.Providers.Ollama;

public sealed class OllamaProviderExtension : IProviderExtension
{
    private readonly string _baseUrl;

    public OllamaProviderExtension(string baseUrl = "http://localhost:11434")
    {
        _baseUrl = baseUrl;
    }

    public string ProviderName => "Ollama";

    public bool IsApplicable() => true;

    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
            OllamaApiClient client = new(NormalizeBaseUri(_baseUrl));
            await client.ListLocalModelsAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Task EnsureRunningAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "Ollama must be started manually. See https://ollama.com for installation instructions.");

    public async Task<IReadOnlyList<DmonModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            OllamaApiClient client = new(NormalizeBaseUri(_baseUrl));
            OllamaProviderFactory factory = new();
            IEnumerable<OllamaSharp.Models.Model> remoteModels =
                await client.ListLocalModelsAsync(cancellationToken).ConfigureAwait(false);
            List<DmonModelInfo> models = [];
            foreach (OllamaSharp.Models.Model model in remoteModels)
            {
                if (string.IsNullOrEmpty(model.Name))
                    continue;
                models.Add(new DmonModelInfo { Id = model.Name, Capabilities = factory.GetCapabilities(model.Name) });
            }
            return models;
        }
        catch
        {
            return [];
        }
    }

    public IProviderFactory CreateFactory() => new OllamaProviderFactory();

    private static Uri NormalizeBaseUri(string url)
    {
        const string fallback = "http://localhost:11434";
        string raw = string.IsNullOrWhiteSpace(url) ? fallback : url.TrimEnd('/');
        if (raw.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            raw = raw[..^4];
        return Uri.TryCreate(raw, UriKind.Absolute, out Uri? parsed) && parsed is not null
            ? parsed
            : new Uri(fallback);
    }
}
