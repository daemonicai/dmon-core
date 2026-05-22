using System.Reflection;
using System.Runtime.Loader;
using Daemon.Extensions;
using Daemon.Protocol.Events;
using Microsoft.Extensions.AI;

namespace Daemon.Core.Extensions;

/// <summary>
/// Loads extensions from NuGet packages or local assembly .dll files.
/// Uses collectible <see cref="AssemblyLoadContext"/> for isolation.
/// </summary>
public sealed class NuGetExtensionLoader : IExtensionLoader, IDisposable
{
    private AssemblyLoadContext? _activeContext;

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

        List<IDaemonExtension> extensions = DiscoverExtensions(assembly);

        if (extensions.Count == 0)
        {
            return CreateErrorResult(
                source.Value,
                "load",
                $"No types implementing IDaemonExtension found in '{assembly.GetName().Name}'.");
        }

        IDaemonExtension primary = extensions[0];
        List<AIFunction> allTools = [];

        foreach (IDaemonExtension ext in extensions)
        {
            foreach (AIFunction fn in ext.Tools)
            {
                allTools.Add(fn);
            }
        }

        return new ExtensionLoadResult
        {
            Name = primary.Name,
            Description = primary.Description,
            Tools = allTools,
            SourceKind = "nuget"
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
            ".daemon",
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

    private static List<IDaemonExtension> DiscoverExtensions(Assembly assembly)
    {
        List<IDaemonExtension> results = [];
        Type targetType = typeof(IDaemonExtension);

        try
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                if (!targetType.IsAssignableFrom(type))
                {
                    continue;
                }

                if (type.GetConstructor(Type.EmptyTypes) is null)
                {
                    continue;
                }

                if (Activator.CreateInstance(type) is IDaemonExtension extension)
                {
                    results.Add(extension);
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

        return results;
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