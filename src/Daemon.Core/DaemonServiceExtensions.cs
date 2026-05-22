using Daemon.Core.Auth;
using Daemon.Core.Extensions;
using Daemon.Core.Providers;
using Microsoft.Extensions.DependencyInjection;

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

    private static void TryAddSingleton<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        if (!services.Any(d => d.ServiceType == typeof(TService)))
        {
            services.AddSingleton<TService, TImplementation>();
        }
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
}