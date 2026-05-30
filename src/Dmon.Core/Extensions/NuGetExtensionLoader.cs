using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Dmon.Abstractions.Providers;
using Dmon.Extensions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Dmon.Core.Extensions;

/// <summary>
/// Loads extensions from NuGet packages or local assembly .dll files.
/// Assemblies are loaded into <see cref="AssemblyLoadContext.Default"/> so that
/// contract types (<c>IDmonExtension</c>, <c>IProviderExtension</c>,
/// <c>Microsoft.Extensions.AI</c>) resolve to the same <see cref="Type"/> identity
/// as the host, making reflection discovery correct by construction.
/// </summary>
public sealed class NuGetExtensionLoader : IExtensionLoader
{
    private readonly IServiceProvider _serviceProvider;

    // Process-global registry: one resolver per distinct extension assembly path.
    // Static because AssemblyLoadContext.Default and its Resolving event are
    // process-global; instance-scoped state would cause stale captures and
    // handler accumulation across multiple loader instances (e.g. in tests).
    // ConcurrentDictionary because the Resolving handler fires on any thread.
    // TryAdd enforces first-writer-wins; conflicting-version support is an
    // explicit non-goal (see design.md D2 risks).
    private static readonly ConcurrentDictionary<string, AssemblyDependencyResolver> _resolvers = new();

    // Subscribed once per process (static ctor) so that multiple loader instances
    // (e.g. in test runs) never accumulate duplicate handlers on the static event.
    static NuGetExtensionLoader()
    {
        AssemblyLoadContext.Default.Resolving += ResolveExtensionDependency;
    }

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

        (List<IDmonExtension> toolExtensions,
         List<IProviderExtension> providerExtensions,
         List<IDmonMiddleware> middlewareExtensions) =
            DiscoverAll(assembly, _serviceProvider);

        if (toolExtensions.Count == 0 && providerExtensions.Count == 0 && middlewareExtensions.Count == 0)
        {
            return CreateErrorResult(
                source.Value,
                "load",
                $"No types implementing IDmonExtension, IProviderExtension, or IDmonMiddleware found in '{assembly.GetName().Name}'.");
        }

        // Use the first tool extension as the primary for Name/Description.
        // If there are no tool extensions, synthesise a minimal name from the provider or assembly.
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
        else if (providerExtensions.Count > 0)
        {
            primary = null;
            name = providerExtensions[0].ProviderName;
            description = null;
        }
        else
        {
            // Middleware-only assembly: derive name from the first middleware type.
            primary = null;
            name = middlewareExtensions[0].GetType().Name;
            description = null;
        }

        return new ExtensionLoadResult
        {
            Name = name,
            Description = description,
            Tools = allTools,
            SourceKind = "nuget",
            Extension = primary,
            ProviderExtension = providerExtensions.Count > 0 ? providerExtensions[0] : null,
            Middleware = middlewareExtensions
        };
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

        // Register a resolver for this path before loading so that any
        // transitive dependency probed during load can be found.
        // TryAdd is a no-op if the path was already registered (task 2.3).
        _resolvers.TryAdd(fullPath, new AssemblyDependencyResolver(fullPath));

        // Load the assembly into the Default context so contract-type identity is
        // shared with the host.  An already-loaded assembly is returned from cache.
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);

        await ValueTask.CompletedTask;
        return assembly;
    }

    // Static handler subscribed once per process (static ctor).  Iterates all
    // accumulated resolvers so the event subscription count stays at exactly one
    // regardless of how many loader instances exist.
    // Note: when multiple resolvers match the same name, the first match wins;
    // selection among them is unordered.  First-writer-wins is enforced by the
    // runtime's assembly cache, not this method — conflicting-version coexistence
    // is an explicit non-goal.
    private static Assembly? ResolveExtensionDependency(AssemblyLoadContext context, AssemblyName name)
    {
        // Pass 1: consult each resolver's .deps.json metadata.
        foreach (AssemblyDependencyResolver resolver in _resolvers.Values)
        {
            string? resolvedPath = resolver.ResolveAssemblyToPath(name);
            if (resolvedPath is not null)
            {
                return AssemblyLoadContext.Default.LoadFromAssemblyPath(resolvedPath);
            }
        }

        // Pass 2: probe each extension's own directory for a sibling .dll.
        // Covers extensions that ship dependencies alongside the main assembly
        // without a full .deps.json manifest.
        if (name.Name is not null)
        {
            foreach (string extensionPath in _resolvers.Keys)
            {
                string siblingPath = Path.Combine(
                    Path.GetDirectoryName(extensionPath)!,
                    name.Name + ".dll");

                if (File.Exists(siblingPath))
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(siblingPath);
                }
            }
        }

        // Return null to let the runtime continue its normal probing chain.
        return null;
    }

    private static (List<IDmonExtension> tools, List<IProviderExtension> providers, List<IDmonMiddleware> middleware)
        DiscoverAll(Assembly assembly, IServiceProvider serviceProvider)
    {
        List<IDmonExtension> tools = [];
        List<IProviderExtension> providers = [];
        List<IDmonMiddleware> middleware = [];
        Type toolType = typeof(IDmonExtension);
        Type providerType = typeof(IProviderExtension);
        Type middlewareType = typeof(IDmonMiddleware);
        Type spType = typeof(IServiceProvider);

        // Resolve a logger once; null if the container does not provide one (e.g. in tests).
        ILogger<NuGetExtensionLoader>? logger =
            serviceProvider.GetService<ILogger<NuGetExtensionLoader>>();

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

                // Middleware: must implement IDmonMiddleware AND carry [DmonMiddleware].
                // A type implementing the interface without the attribute is silently ignored.
                bool isMiddleware = middlewareType.IsAssignableFrom(type)
                    && type.IsDefined(typeof(DmonMiddlewareAttribute), inherit: false);

                if (!isToolExt && !isProviderExt && !isMiddleware)
                {
                    continue;
                }

                // Tool and provider instantiation: let exceptions propagate so that
                // ExtensionService.LoadAsync can surface them as ExtensionErrorEvents.
                // This preserves the original semantics — a throwing tool/provider ctor
                // fails the entire assembly load.
                //
                // Middleware-only instantiation is wrapped in a try/catch: a throwing
                // middleware ctor is logged and skipped; the rest of the assembly still loads.
                // This is intentionally distinct from tool/provider behaviour.
                //
                // Dual types (implementing tool/provider AND middleware): instantiated once
                // on the tool/provider path (propagating); the instance is then shared with
                // the middleware list if it also satisfies IDmonMiddleware.
                object? instance = null;

                if (isToolExt || isProviderExt)
                {
                    // Propagating path — exceptions reach the caller.
                    if (type.GetConstructor([spType]) is not null)
                    {
                        instance = Activator.CreateInstance(type, serviceProvider);
                    }
                    else if (type.GetConstructor(Type.EmptyTypes) is not null)
                    {
                        instance = Activator.CreateInstance(type);
                    }
                }
                else
                {
                    // Pure middleware path — catch and skip on ctor failure.
                    try
                    {
                        if (type.GetConstructor([spType]) is not null)
                        {
                            instance = Activator.CreateInstance(type, serviceProvider);
                        }
                        else if (type.GetConstructor(Type.EmptyTypes) is not null)
                        {
                            instance = Activator.CreateInstance(type);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Per spec "Middleware constructor throws — startup continues":
                        // log the failure, skip this middleware, and continue loading.
                        Exception inner = ex.InnerException ?? ex;

                        logger?.LogWarning(
                            inner,
                            "Skipping middleware type '{TypeName}' in assembly '{AssemblyName}': constructor threw an exception.",
                            type.FullName,
                            assembly.GetName().Name);

                        continue;
                    }
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

                if (isMiddleware && instance is IDmonMiddleware mw)
                {
                    middleware.Add(mw);
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

        return (tools, providers, middleware);
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