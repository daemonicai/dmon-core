using Dmon.Abstractions.Providers;
using Dmon.Core.Providers;
using Dmon.Extensions;
using Dmon.Protocol.Events;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Extensions;

/// <summary>
/// Orchestrates extension.load, extension.unload, and extension.promote commands.
/// Routes to the correct loader based on parsed source kind, manages the tool registry,
/// and emits <see cref="ExtensionLoadedEvent"/>/<see cref="ExtensionUnloadedEvent"/>/
/// <see cref="ExtensionErrorEvent"/>.
/// </summary>
public sealed class ExtensionService
{
    private readonly IToolRegistry _toolRegistry;
    private readonly IProviderRegistry? _providerRegistry;
    private readonly Dictionary<string, IExtensionLoader> _loaders;
    private readonly ILogger<ExtensionService> _logger;

    /// <summary>
    /// Raised when an <see cref="ExtensionLoadedEvent"/> is produced.
    /// </summary>
    public event Action<ExtensionLoadedEvent>? Loaded;

    /// <summary>
    /// Raised when an <see cref="ExtensionUnloadedEvent"/> is produced.
    /// </summary>
    public event Action<ExtensionUnloadedEvent>? Unloaded;

    /// <summary>
    /// Raised when an <see cref="ExtensionErrorEvent"/> is produced.
    /// </summary>
    public event Action<ExtensionErrorEvent>? Error;

    public ExtensionService(
        IToolRegistry toolRegistry,
        IEnumerable<IExtensionLoader> loaders,
        ILogger<ExtensionService> logger,
        IProviderRegistry? providerRegistry = null)
    {
        _toolRegistry = toolRegistry;
        _providerRegistry = providerRegistry;
        _logger = logger;
        _loaders = loaders.ToDictionary(l => l.SourceKind, StringComparer.OrdinalIgnoreCase);

        // The NuGetExtensionLoader handles both "nuget" package sources and
        // "assembly" (.dll) sources, but only advertises SourceKind="nuget".
        // Register it under "assembly" too so local .dll paths resolve.
        if (_loaders.TryGetValue("nuget", out IExtensionLoader? nugetLoader)
            && !_loaders.ContainsKey("assembly"))
        {
            _loaders["assembly"] = nugetLoader;
        }
    }

    /// <summary>
    /// Loads an extension from the given RPC command source string.
    /// Emits <see cref="ExtensionLoadedEvent"/> on success or
    /// <see cref="ExtensionErrorEvent"/> on failure.
    /// No partial registration — all or nothing.
    /// </summary>
    /// <remarks>
    /// Extension registration (both tool and provider) must happen strictly between turns —
    /// never during an active streaming call. The turn model is single-threaded, so no
    /// locking is needed, but callers must ensure this method is not invoked concurrently
    /// with an in-flight LLM streaming call.
    /// </remarks>
    public async Task LoadAsync(string source, CancellationToken cancellationToken = default)
    {
        ParsedExtensionSource parsed = ParsedExtensionSource.Parse(source);

        if (!_loaders.TryGetValue(parsed.Kind, out IExtensionLoader? loader))
        {
            Error?.Invoke(new ExtensionErrorEvent
            {
                Source = source,
                Phase = "parse",
                Diagnostics = [$"Unknown source kind '{parsed.Kind}'. Expected 'nuget:', 'assembly', or '.csx'."]
            });
            return;
        }

        ExtensionLoadResult result;

        try
        {
            result = await loader.LoadAsync(parsed, cancellationToken);
        }
        catch (Exception ex)
        {
            Error?.Invoke(new ExtensionErrorEvent
            {
                Source = source,
                Phase = "execute",
                Diagnostics = [ex.Message]
            });
            return;
        }

        // Check for error sentinel.
        if (result.Name.StartsWith("__error__", StringComparison.Ordinal))
        {
            string description = result.Description ?? "Unknown error";
            string phase = "load";

            if (description.StartsWith("ERROR[", StringComparison.Ordinal))
            {
                int closeBracket = description.IndexOf(']', 6);
                if (closeBracket > 6)
                {
                    phase = description[6..closeBracket];
                }
            }

            Error?.Invoke(new ExtensionErrorEvent
            {
                Source = source,
                Phase = phase,
                Diagnostics = [description]
            });
            return;
        }

        // Route provider extension if present.
        string? registeredProviderName = null;

        if (result.ProviderExtension is IProviderExtension providerExt)
        {
            if (!providerExt.IsApplicable())
            {
                _logger.LogWarning(
                    "Provider extension '{Name}' is not applicable on this platform and will not be activated.",
                    providerExt.ProviderName);
            }
            else if (_providerRegistry is null)
            {
                _logger.LogWarning(
                    "Provider extension '{Name}' cannot be registered: no IProviderRegistry is available.",
                    providerExt.ProviderName);
            }
            else
            {
                await _providerRegistry.RegisterExtensionAsync(providerExt, cancellationToken);
                registeredProviderName = providerExt.ProviderName;
            }
        }

        // All or nothing — require tools or a registered provider.
        if (result.Tools.Count == 0 && registeredProviderName is null)
        {
            Error?.Invoke(new ExtensionErrorEvent
            {
                Source = source,
                Phase = "load",
                Diagnostics = ["Extension provided no AIFunction instances and no applicable provider."]
            });
            return;
        }

        if (result.Tools.Count > 0)
        {
            IDmonExtension extension = result.Extension
                ?? new AnonymousExtension(result.Name, result.Description ?? result.Name, result.Tools);
            _toolRegistry.Register(result.Name, extension, result.Tools);
        }

        Loaded?.Invoke(new ExtensionLoadedEvent
        {
            Name = result.Name,
            Tools = result.Tools.Select(f => f.Name).ToList(),
            ProviderName = registeredProviderName
        });
    }

    /// <summary>
    /// Deregisters the named extension's tools from the tool registry so they are no longer
    /// offered to the LLM, and emits <see cref="ExtensionUnloadedEvent"/>.
    /// The extension's assembly is NOT unloaded — it remains resident in the process until
    /// the core is restarted. To reclaim memory, restart the core process.
    /// Does not throw if the extension is not registered.
    /// </summary>
    public void Unload(string name)
    {
        _toolRegistry.Unregister(name);
        Unloaded?.Invoke(new ExtensionUnloadedEvent { Name = name });
    }

    /// <summary>
    /// Returns a snapshot of all registered extensions and their tool counts.
    /// </summary>
    public IReadOnlyList<RegisteredExtensionSnapshot> GetSnapshot() => _toolRegistry.GetSnapshot();

    /// <summary>
    /// Removes all registered tools from the registry.
    /// </summary>
    public void Clear() => _toolRegistry.Clear();
}
