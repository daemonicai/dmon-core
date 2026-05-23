using System.ClientModel;
using Anthropic.SDK;
using Dmon.Protocol.Events;
using GeminiDotnet;
using GeminiDotnet.Extensions.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace Dmon.Core.Providers;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly IReadOnlyList<ProviderConfig> _all;
    private readonly ICredentialResolver _credentials;
    private readonly ILogger<ProviderRegistry> _logger;

    private int _activeIndex;
    private IChatClient? _activeClient;

    // pending switch: index + optional override model id
    private int? _pendingIndex;
    private string? _pendingModelId;

    public ProviderRegistry(
        IEnumerable<ProviderConfig> configs,
        ICredentialResolver credentials,
        ILogger<ProviderRegistry> logger)
    {
        _all = configs.ToList();
        _credentials = credentials;
        _logger = logger;

        if (_all.Count == 0)
        {
            throw new InvalidOperationException("At least one provider must be configured.");
        }

        _activeIndex = 0;
    }

    public IReadOnlyList<ProviderConfig> GetAll() => _all;

    public ProviderConfig GetCurrentConfig() => _all[_activeIndex];

    public bool CurrentSupportsToolCalling => GetCurrentConfig().Capabilities.ToolCalling;

    public bool CurrentSupportsReasoning => GetCurrentConfig().Capabilities.Reasoning;

    public async ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_activeClient is null)
        {
            _activeClient = await CreateClientAsync(_all[_activeIndex], cancellationToken).ConfigureAwait(false);
        }

        return _activeClient;
    }

    public void SetProvider(string name, string? modelId = null)
    {
        int index = FindProviderIndex(name);
        _pendingIndex = index;
        _pendingModelId = modelId;
        _logger.LogDebug("Provider switch to {Provider} queued (effective next turn).", name);
    }

    public void CycleProvider()
    {
        int next = ((_pendingIndex ?? _activeIndex) + 1) % _all.Count;
        _pendingIndex = next;
        _pendingModelId = null;
        _logger.LogDebug("Provider cycle queued to {Provider} (effective next turn).", _all[next].Name);
    }

    /// <inheritdoc/>
    public ProviderSwitchedEvent? CommitPendingSwitch()
    {
        if (_pendingIndex is null)
        {
            return null;
        }

        int newIndex = _pendingIndex.Value;
        string? overrideModelId = _pendingModelId;

        _pendingIndex = null;
        _pendingModelId = null;

        _activeClient?.Dispose();
        _activeClient = null;

        _activeIndex = newIndex;

        ProviderConfig newConfig = _all[_activeIndex];
        string activeModelId = overrideModelId ?? newConfig.DefaultModelId ?? string.Empty;

        return new ProviderSwitchedEvent
        {
            Name = newConfig.Name,
            Model = activeModelId,
            EffectiveNextTurn = true
        };
    }

    private int FindProviderIndex(string name)
    {
        for (int i = 0; i < _all.Count; i++)
        {
            if (string.Equals(_all[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Provider '{name}' is not configured.");
    }

    private async ValueTask<IChatClient> CreateClientAsync(ProviderConfig config, CancellationToken cancellationToken)
    {
        string? apiKey = null;

        if (!string.Equals(config.Auth.Type, "none", StringComparison.OrdinalIgnoreCase))
        {
            apiKey = await _credentials.ResolveAsync(config.Name, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("No API key resolved for provider {Provider}.", config.Name);
            }
        }

        string modelId = config.DefaultModelId ?? string.Empty;

        return config.Adapter.ToLowerInvariant() switch
        {
            "openai" => CreateOpenAiClient(config, apiKey, modelId),
            "anthropic" => CreateAnthropicClient(config, apiKey, modelId),
            "gemini" => CreateGeminiClient(config, apiKey, modelId),
            _ => throw new InvalidOperationException($"Unknown adapter '{config.Adapter}' for provider '{config.Name}'.")
        };
    }

    private static IChatClient CreateOpenAiClient(ProviderConfig config, string? apiKey, string modelId)
    {
        OpenAIClientOptions options = new();

        if (config.BaseUrl is not null)
        {
            options.Endpoint = new Uri(config.BaseUrl);
        }

        ChatClient chatClient;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Use a placeholder key for auth-type=none endpoints (e.g. Ollama)
            chatClient = new ChatClient(modelId, new ApiKeyCredential("none"), options);
        }
        else
        {
            chatClient = new ChatClient(modelId, new ApiKeyCredential(apiKey), options);
        }

        return chatClient.AsIChatClient();
    }

    private static IChatClient CreateAnthropicClient(ProviderConfig config, string? apiKey, string modelId)
    {
        AnthropicClient client = string.IsNullOrWhiteSpace(apiKey)
            ? new AnthropicClient()
            : new AnthropicClient(apiKey);

        if (config.BaseUrl is not null)
        {
            // ApiUrlFormat is "{baseUrl}/{0}/{1}" where {0}=version, {1}=endpoint path.
            client.ApiUrlFormat = $"{config.BaseUrl.TrimEnd('/')}/{{0}}/{{1}}";
        }

        // MessagesEndpoint implements IChatClient directly.
        return client.Messages;
    }

    /// <remarks>
    /// GeminiDotnet.Extensions.AI does not expose a base-URL override via <see cref="GeminiClientOptions"/> as of v0.25.0.
    /// If oMLX Gemini-compatible support is required, revisit when the package exposes an <c>Endpoint</c> property or
    /// switch to the OpenAI adapter pointed at the Gemini REST base URL.
    /// </remarks>
    private static IChatClient CreateGeminiClient(ProviderConfig config, string? apiKey, string modelId)
    {
        GeminiClientOptions options = new()
        {
            ApiKey = apiKey ?? string.Empty,
            ModelId = string.IsNullOrWhiteSpace(modelId) ? "gemini-2.0-flash" : modelId
        };

        if (config.BaseUrl is not null)
        {
            options.Endpoint = new Uri(config.BaseUrl);
        }

        return new GeminiChatClient(options);
    }
}
