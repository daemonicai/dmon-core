using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Providers;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly IReadOnlyList<ProviderConfig> _all;
    private readonly Dictionary<string, IProviderFactory> _factories;
    private readonly ICredentialResolver _credentials;
    private readonly ILogger<ProviderRegistry> _logger;

    private int _activeIndex;
    private IChatClient? _activeClient;
    private int? _pendingIndex;
    private string? _pendingModelId;

    public ProviderRegistry(
        IEnumerable<ProviderConfig> configs,
        IEnumerable<IProviderFactory> factories,
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

        _factories = factories.ToDictionary(
            f => f.AdapterName,
            f => f,
            StringComparer.OrdinalIgnoreCase);

        foreach (ProviderConfig config in _all)
        {
            if (!_factories.ContainsKey(config.Adapter))
            {
                throw new InvalidOperationException(
                    $"No factory registered for adapter '{config.Adapter}' (provider '{config.Name}').");
            }
        }

        _activeIndex = 0;
    }

    public IReadOnlyList<ProviderConfig> GetAll() => _all;

    public ProviderConfig GetCurrentConfig() => _all[_activeIndex];

    public bool CurrentSupportsToolCalling
    {
        get
        {
            if (_activeClient?.GetService(typeof(ChatClientCapabilities)) is ChatClientCapabilities caps)
                return caps.SupportsToolCalling;
            ProviderConfig config = GetCurrentConfig();
            return _factories[config.Adapter].GetCapabilities(config.DefaultModelId ?? string.Empty).SupportsToolCalling;
        }
    }

    public bool CurrentSupportsReasoning
    {
        get
        {
            if (_activeClient?.GetService(typeof(ChatClientCapabilities)) is ChatClientCapabilities caps)
                return caps.SupportsReasoning;
            ProviderConfig config = GetCurrentConfig();
            return _factories[config.Adapter].GetCapabilities(config.DefaultModelId ?? string.Empty).SupportsReasoning;
        }
    }

    public async ValueTask<IChatClient> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_activeClient is null)
        {
            _activeClient = await CreateClientAsync(_all[_activeIndex], cancellationToken).ConfigureAwait(false);
        }

        return _activeClient;
    }

    public void SetProvider(string name)
    {
        int index = FindProviderIndex(name);
        _pendingIndex = index;
        _logger.LogDebug("Provider switch to {Provider} queued (effective next turn).", name);
    }

    public void SetModel(string modelId)
    {
        _pendingModelId = modelId;

        int currentOrPendingIndex = _pendingIndex ?? _activeIndex;
        ProviderConfig config = _all[currentOrPendingIndex];
        IProviderFactory factory = _factories[config.Adapter];
        ChatClientCapabilities capabilities = factory.GetCapabilities(modelId);

        if (!capabilities.SupportsToolCalling && !capabilities.SupportsReasoning && capabilities.ContextWindow == 0)
        {
            _logger.LogWarning(
                "Model '{ModelId}' is not recognised by factory '{Adapter}'; capabilities will use safe defaults.",
                modelId,
                config.Adapter);
        }

        _logger.LogDebug("Model switch to {ModelId} queued (effective next turn).", modelId);
    }

    public void CycleProvider()
    {
        int next = ((_pendingIndex ?? _activeIndex) + 1) % _all.Count;
        _pendingIndex = next;
        _pendingModelId = null;
        _logger.LogDebug("Provider cycle queued to {Provider} (effective next turn).", _all[next].Name);
    }

    public ProviderSwitchResult? CommitPendingSwitch()
    {
        if (_pendingIndex is null && _pendingModelId is null)
        {
            return null;
        }

        int newIndex = _pendingIndex ?? _activeIndex;
        string? overrideModelId = _pendingModelId;

        _pendingIndex = null;
        _pendingModelId = null;

        _activeClient?.Dispose();
        _activeClient = null;

        _activeIndex = newIndex;

        ProviderConfig newConfig = _all[_activeIndex];
        string activeModelId = overrideModelId ?? newConfig.DefaultModelId ?? string.Empty;

        return new ProviderSwitchResult(newConfig.Name, activeModelId);
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

        return await _factories[config.Adapter].CreateAsync(config, apiKey, cancellationToken).ConfigureAwait(false);
    }
}
