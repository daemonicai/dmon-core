using Daemon.Core.Auth;
using Daemon.Core.Bootstrap;
using Daemon.Core.Extensions;
using Daemon.Core.Permissions;
using Daemon.Core.Providers;
using Daemon.Core.Rpc;
using Daemon.Core.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Daemon.Core;

public static class DaemonServiceExtensions
{
    /// <summary>
    /// Registers provider-related services: configuration loading, credential
    /// resolution, and the provider registry.
    /// </summary>
    public static IServiceCollection AddDaemonProviders(this IServiceCollection services)
    {
        services.AddSingleton<ProviderConfigLoader>();
        services.AddSingleton(sp =>
        {
            ProviderConfigLoader loader = sp.GetRequiredService<ProviderConfigLoader>();
            return loader.Load();
        });

        services.AddSingleton<ICredentialFileStore, CredentialFileStore>();
        services.AddSingleton<ICredentialResolver, CredentialResolver>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        return services;
    }

    /// <summary>
    /// Registers authentication services: credential file store and the
    /// auth command handler.
    /// </summary>
    public static IServiceCollection AddDaemonAuth(this IServiceCollection services)
    {
        // ICredentialFileStore is registered in AddDaemonProviders — only
        // add it here if providers haven't been registered yet.
        services.TryAddSingleton<ICredentialFileStore, CredentialFileStore>();
        services.AddSingleton<IAuthService, AuthService>();

        return services;
    }

    /// <summary>
    /// Registers extension loading services: tool registry, loaders, extension service,
    /// and promote service.
    /// </summary>
    public static IServiceCollection AddDaemonExtensions(this IServiceCollection services)
    {
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<CsxScriptLoader>();
        services.AddSingleton<NuGetExtensionLoader>();
        services.AddSingleton<IExtensionLoader>(sp => sp.GetRequiredService<CsxScriptLoader>());
        services.AddSingleton<IExtensionLoader>(sp => sp.GetRequiredService<NuGetExtensionLoader>());
        services.AddSingleton<ExtensionService>();
        services.AddSingleton<PromoteService>();

        return services;
    }

    /// <summary>
    /// Registers the RPC infrastructure: event emitter, command dispatcher,
    /// turn handler, stub handlers, and bootstrap service.
    /// Call after AddDaemonProviders, AddDaemonAuth, and AddDaemonExtensions.
    /// </summary>
    public static IServiceCollection AddDaemonCore(this IServiceCollection services)
    {
        services.AddSingleton<ISessionDirectoryResolver, SessionDirectoryResolver>();
        services.AddSingleton<ISessionStore, SessionStore>();

        // Permission runtime dependencies
        services.AddSingleton<IPermissionSettings>(_ =>
            PermissionSettingsLoader.LoadProject(Directory.GetCurrentDirectory()));
        services.AddSingleton<IBashCompositeDetector, BashCompositeDetector>();
        services.AddSingleton<IDenylistChecker, DenylistChecker>();
        services.AddSingleton<IPermissionPolicy>(sp =>
        {
            IPermissionSettings project = sp.GetRequiredService<IPermissionSettings>();
            IBashCompositeDetector compositeDetector = sp.GetRequiredService<IBashCompositeDetector>();
            IDenylistChecker denylist = sp.GetRequiredService<IDenylistChecker>();
            // Note: project and global are the same instance until distinct registrations
            // for project vs global settings are implemented.
            return new PermissionPolicy(
                project,
                project,
                Directory.GetCurrentDirectory(),
                compositeDetector,
                denylist);
        });

        services.AddSingleton<IEventEmitter>(_ => new EventEmitter(Console.Out));

        services.AddSingleton<TurnHandler>();
        services.AddSingleton<ITurnHandler>(sp => sp.GetRequiredService<TurnHandler>());

        services.AddSingleton<IModelHandler, NullModelHandler>();
        services.AddSingleton<SessionHandler>();
        services.AddSingleton<ISessionHandler>(sp => sp.GetRequiredService<SessionHandler>());
        services.AddSingleton<IExtensionHandler, NullExtensionHandler>();
        services.AddSingleton<IAuthHandler, NullAuthHandler>();
        services.AddSingleton<ThinkingHandler>();
        services.AddSingleton<IThinkingHandler>(sp => sp.GetRequiredService<ThinkingHandler>());

        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<BootstrapService>();

        return services;
    }
}
