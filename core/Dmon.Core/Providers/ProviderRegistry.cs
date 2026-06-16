using Dmon.Abstractions.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Providers;

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly IReadOnlyList<ProviderConfig> _builtIn;
    private readonly List<ProviderConfig> _extensionConfigs = [];
    private readonly Dictionary<string, IProviderFactory> _factories;
    private readonly ICredentialResolver _credentials;
    private readonly ILogger<ProviderRegistry> _logger;

    private int _activeIndex;
    private IChatClient? _activeClient;
    private int? _pendingIndex;
    private string? _pendingModelId;
    private string? _activeModelId;

    public ProviderRegistry(
        IEnumerable<ProviderConfig> configs,
        IEnumerable<IProviderFactory> factories,
        ICredentialResolver credentials,
        IActiveModelStore store,
        ILogger<ProviderRegistry> logger)
    {
        _credentials = credentials;
        _logger = logger;

        _factories = factories.ToDictionary(
            f => f.AdapterName,
            f => f,
            StringComparer.OrdinalIgnoreCase);

        // Filter configs to only those whose adapter has a registered factory.
        // With the granular provider-package split (ADR-023 D7), the composition root
        // decides which providers to compose; a config entry for an unregistered adapter
        // is silently skipped (with a warning) rather than failing startup.
        List<ProviderConfig> supported = [];
        foreach (ProviderConfig config in configs)
        {
            if (_factories.ContainsKey(config.Adapter))
            {
                supported.Add(config);
            }
            else
            {
                _logger.LogWarning(
                    "Provider '{Name}' (adapter: '{Adapter}') is in config but no factory is registered. " +
                    "Add the corresponding Use<Provider>() verb to your Dmon.cs composition root.",
                    config.Name, config.Adapter);
            }
        }
        _builtIn = supported;

        _activeIndex = 0;

        // Restore the last active selection persisted across restarts.
        // NOTE: Extension/dynamic providers registered after construction are not restorable here —
        // they are not yet in GetAll() at ctor time. That is a known limitation.
        ModelRef? saved = store.Load();
        if (saved is not null)
        {
            IReadOnlyList<ProviderConfig> all = GetAll();
            int found = -1;
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Name, saved.Provider, StringComparison.OrdinalIgnoreCase))
                {
                    found = i;
                    break;
                }
            }

            if (found >= 0)
            {
                _activeIndex = found;
                _activeModelId = saved.Model;
                _logger.LogDebug("Restored active provider '{Provider}' (model: {Model}) from config.",
                    saved.Provider, saved.Model ?? "(default)");
            }
            else
            {
                _logger.LogDebug(
                    "Persisted provider '{Provider}' is not configured; keeping default (index 0).",
                    saved.Provider);
            }
        }
    }

    public IReadOnlyList<ProviderConfig> GetAll()
    {
        if (_extensionConfigs.Count == 0)
            return _builtIn;

        List<ProviderConfig> combined = new(_builtIn.Count + _extensionConfigs.Count);
        combined.AddRange(_builtIn);
        combined.AddRange(_extensionConfigs);
        return combined;
    }

    public Task RegisterExtensionAsync(
        IProviderExtension extension,
        CancellationToken cancellationToken = default)
    {
        IProviderFactory factory = extension.CreateFactory();

        // Do NOT call ListModelsAsync here — it is a network call (ADR-007: registration
        // must be cheap and local). Model enumeration is deferred to on-demand paths
        // (ModelModelsHandler, setup wizard). Use the factory's static default instead.
        ProviderConfig config = new()
        {
            Name = extension.ProviderName,
            Adapter = factory.AdapterName,
            DefaultModelId = factory.DefaultModelId,
            Auth = new ProviderAuthConfig { Type = "none" }
        };

        _factories[factory.AdapterName] = factory;

        int existingIndex = _extensionConfigs.FindIndex(
            c => string.Equals(c.Name, extension.ProviderName, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _extensionConfigs[existingIndex] = config;
        else
            _extensionConfigs.Add(config);

        _logger.LogDebug("Registered extension provider '{Provider}' (adapter: {Adapter}).",
            extension.ProviderName, factory.AdapterName);

        return Task.CompletedTask;
    }

    public void AddDynamicProvider(ProviderConfig config)
    {
        if (!_factories.ContainsKey(config.Adapter))
        {
            throw new InvalidOperationException(
                $"No factory registered for adapter '{config.Adapter}'.");
        }

        int existingIndex = _extensionConfigs.FindIndex(
            c => string.Equals(c.Name, config.Name, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
            _extensionConfigs[existingIndex] = config;
        else
            _extensionConfigs.Add(config);

        _logger.LogDebug("Added dynamic provider '{Provider}' (adapter: {Adapter}).",
            config.Name, config.Adapter);
    }

    public string? GetCurrentModelId() => _activeModelId;

    public ProviderConfig GetCurrentConfig()
    {
        IReadOnlyList<ProviderConfig> all = GetAll();
        EnsureProviderConfigured(all);
        return all[_activeIndex];
    }

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
        IReadOnlyList<ProviderConfig> all = GetAll();
        EnsureProviderConfigured(all);

        if (_activeClient is null)
        {
            _activeClient = await CreateClientAsync(all[_activeIndex], cancellationToken).ConfigureAwait(false);
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

        IReadOnlyList<ProviderConfig> all = GetAll();
        int currentOrPendingIndex = _pendingIndex ?? _activeIndex;
        ProviderConfig config = all[currentOrPendingIndex];
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
        IReadOnlyList<ProviderConfig> all = GetAll();
        int next = ((_pendingIndex ?? _activeIndex) + 1) % all.Count;
        _pendingIndex = next;
        _pendingModelId = null;
        _logger.LogDebug("Provider cycle queued to {Provider} (effective next turn).", all[next].Name);
    }

    public ProviderSwitchResult? CommitPendingSwitch()
    {
        if (_pendingIndex is null && _pendingModelId is null)
        {
            return null;
        }

        IReadOnlyList<ProviderConfig> all = GetAll();
        int newIndex = _pendingIndex ?? _activeIndex;
        string? overrideModelId = _pendingModelId;

        _pendingIndex = null;
        _pendingModelId = null;

        _activeClient?.Dispose();
        _activeClient = null;

        _activeIndex = newIndex;

        ProviderConfig newConfig = all[_activeIndex];
        string activeModelId = overrideModelId ?? newConfig.DefaultModelId ?? string.Empty;

        if (overrideModelId is not null)
            _activeModelId = overrideModelId;

        return new ProviderSwitchResult(newConfig.Name, activeModelId);
    }

    private static void EnsureProviderConfigured(IReadOnlyList<ProviderConfig> all)
    {
        if (all.Count == 0)
        {
            throw new InvalidOperationException("At least one provider must be configured.");
        }
    }

    private int FindProviderIndex(string name)
    {
        IReadOnlyList<ProviderConfig> all = GetAll();
        for (int i = 0; i < all.Count; i++)
        {
            if (string.Equals(all[i].Name, name, StringComparison.OrdinalIgnoreCase))
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
