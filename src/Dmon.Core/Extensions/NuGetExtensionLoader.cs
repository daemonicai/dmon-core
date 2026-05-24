using System.Reflection;
using System.Runtime.Loader;
using Dmon.Abstractions.Providers;
using Dmon.Extensions;
using Microsoft.Extensions.AI;

namespace Dmon.Core.Extensions;

/// <summary>
/// Loads extensions from NuGet packages or local assembly .dll files.
/// Uses collectible <see cref="AssemblyLoadContext"/> for isolation.
/// </summary>
public sealed class NuGetExtensionLoader : IExtensionLoader, IDisposable
{
    private AssemblyLoadContext? _activeContext;
    private readonly IServiceProvider _serviceProvider;

    public NuGetExtensionLoader(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public string SourceKind => "nuget";

    public Func<ExtensionLoadConfirmRequest, CancellationToken, Task<bool>>? ConfirmCallback { get; set; }

    public async Task<ExtensionLoadResult> LoadAsync(
        ParsedExtensionSource source,
        CancellationToken cancellationToken = default)
    {
        if (source.Kind == "nuget" && ConfirmCallback is not null)
        {
            // Surface to permission gate BEFORE any network call or assembly load.
            // Phase "resolve" covers NuGet package download.
            ExtensionLoadConfirmRequest resolveRequest = new()
            {
                Source = source.Value,
                Phase = "resolve",
                PackageId = source.Value,
                PackageVersion = source.Version,
                Description = $"NuGet package '{source.Value}'"
                    + (source.Version is null ? " (latest)" : $" version {source.Version}")
            };

            bool confirmed = await ConfirmCallback(resolveRequest, cancellationToken);

            if (!confirmed)
            {
                return CreateErrorResult(
                    source.Value,
                    "resolve",
                    $"Permission denied for NuGet package '{source.Value}'.");
            }
        }

        Assembly assembly;

        if (source.Kind == "nuget")
        {
            assembly = await LoadNuGetPackageAsync(source, cancellationToken);
        }
        else
        {
            assembly = await LoadLocalAssemblyAsync(source.Value, cancellationToken);
        }

        // Phase "load" covers loading the assembly into the process.
        if (ConfirmCallback is not null)
        {
            ExtensionLoadConfirmRequest loadRequest = new()
            {
                Source = source.Value,
                Phase = "load",
                Description = $"Load assembly '{assembly.GetName().Name}' into process"
            };

            bool confirmed = await ConfirmCallback(loadRequest, cancellationToken);

            if (!confirmed)
            {
                return CreateErrorResult(
                    source.Value,
                    "load",
                    $"Permission denied for assembly '{assembly.GetName().Name}'.");
            }
        }

        (List<IDmonExtension> toolExtensions, List<IProviderExtension> providerExtensions) =
            DiscoverAll(assembly, _serviceProvider);

        if (toolExtensions.Count == 0 && providerExtensions.Count == 0)
        {
            return CreateErrorResult(
                source.Value,
                "load",
                $"No types implementing IDmonExtension or IProviderExtension found in '{assembly.GetName().Name}'.");
        }

        // Use the first tool extension as the primary for Name/Description.
        // If there are no tool extensions, synthesise a minimal name from the assembly.
        string name;
        string? description;
        IDmonExtension? primary;
        List<AIFunction> allTools = [];

        if (toolExtensions.Count > 0)
        {
            primary = toolExtensions[0];
            name = primary.Name;
            description = primary.Description;

            foreach (IDmonExtension ext in toolExtensions)
            {
                foreach (AIFunction fn in ext.Tools)
                {
                    allTools.Add(fn);
                }
            }
        }
        else
        {
            primary = null;
            name = providerExtensions[0].ProviderName;
            description = null;
        }

        return new ExtensionLoadResult
        {
            Name = name,
            Description = description,
            Tools = allTools,
            SourceKind = "nuget",
            Extension = primary,
            ProviderExtension = providerExtensions.Count > 0 ? providerExtensions[0] : null
        };
    }

    public void Dispose()
    {
        if (_activeContext is not null && _activeContext.IsCollectible)
        {
            _activeContext.Unload();
        }

        _activeContext = null;
    }

    private async Task<Assembly> LoadNuGetPackageAsync(
        ParsedExtensionSource source,
        CancellationToken cancellationToken)
    {
        // NuGet resolution deferred to V1.1 — see ADR-002 for planned integration.
        // For V1, NuGet packages must be pre-resolved via `dotnet add package` or
        // placed in the extensions cache directory.
        string cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dmon",
            "extensions",
            source.Value);

        if (source.Version is not null)
        {
            cacheRoot = Path.Combine(cacheRoot, source.Version);
        }

        if (Directory.Exists(cacheRoot))
        {
            string[] dllFiles = Directory.GetFiles(cacheRoot, "*.dll", SearchOption.AllDirectories);

            if (dllFiles.Length > 0)
            {
                return await LoadLocalAssemblyAsync(dllFiles[0], cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"NuGet package '{source.Value}' is not cached. For V1, place the resolved " +
            $"package assemblies in '{cacheRoot}' or use `dotnet add package` to reference " +
            "the extension assembly at build time, then load via an absolute path.");
    }

    private async ValueTask<Assembly> LoadLocalAssemblyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Assembly not found: {fullPath}");
        }

        // Create a new collectible ALC for each load, isolating extension assemblies
        // from the host and from each other.
        _activeContext?.Unload();
        _activeContext = new AssemblyLoadContext($"extension-{Path.GetFileNameWithoutExtension(fullPath)}", isCollectible: true);

        // Load the assembly into the collectible ALC.
        Assembly assembly = _activeContext.LoadFromAssemblyPath(fullPath);

        await ValueTask.CompletedTask;
        return assembly;
    }

    private static (List<IDmonExtension> tools, List<IProviderExtension> providers)
        DiscoverAll(Assembly assembly, IServiceProvider serviceProvider)
    {
        List<IDmonExtension> tools = [];
        List<IProviderExtension> providers = [];
        Type toolType = typeof(IDmonExtension);
        Type providerType = typeof(IProviderExtension);
        Type spType = typeof(IServiceProvider);

        try
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                bool isToolExt = toolType.IsAssignableFrom(type);
                bool isProviderExt = providerType.IsAssignableFrom(type);

                if (!isToolExt && !isProviderExt)
                {
                    continue;
                }

                // A type implementing both interfaces is instantiated once; the same
                // instance is added to both lists.
                object? instance = null;

                if (type.GetConstructor([spType]) is not null)
                {
                    instance = Activator.CreateInstance(type, serviceProvider);
                }
                else if (type.GetConstructor(Type.EmptyTypes) is not null)
                {
                    instance = Activator.CreateInstance(type);
                }

                if (instance is null)
                {
                    continue;
                }

                if (isToolExt && instance is IDmonExtension toolExt)
                {
                    tools.Add(toolExt);
                }

                if (isProviderExt && instance is IProviderExtension providerExt)
                {
                    providers.Add(providerExt);
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Surface as an ExtensionErrorEvent-compatible diagnostic.
            throw new InvalidOperationException(
                $"Failed to discover extensions in assembly '{assembly.GetName().Name}': " +
                string.Join("; ", ex.LoaderExceptions.Select(e => e?.Message ?? "unknown")),
                ex);
        }

        return (tools, providers);
    }

    private static ExtensionLoadResult CreateErrorResult(
        string source,
        string phase,
        string message)
    {
        // Return a result that indicates an error. The caller (ExtensionService)
        // will convert this to an ExtensionErrorEvent.
        return new ExtensionLoadResult
        {
            Name = $"__error__{Guid.NewGuid():N}",
            Description = $"ERROR[{phase}]: {message}",
            Tools = [],
            SourceKind = source
        };
    }
}