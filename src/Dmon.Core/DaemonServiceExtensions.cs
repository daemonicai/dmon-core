using Dmon.Abstractions;
using Dmon.Abstractions.Providers;
using Dmon.Core.Auth;
using Dmon.Core.Config;
using Dmon.Core.GitHub;
using Dmon.Core.SystemPrompt;
using Dmon.Core.Bootstrap;
using Dmon.Core.Extensions;
using Dmon.Core.Permissions;
using Dmon.Protocol.Permissions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Providers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Dmon.Core;

public static class DmonServiceExtensions
{
    /// <summary>
    /// Registers provider-related services: configuration loading, credential
    /// resolution, and the provider registry.
    /// </summary>
    public static IServiceCollection AddDmonProviders(this IServiceCollection services)
    {
        services.AddSingleton<ProviderConfigLoader>();
        services.AddSingleton<IEnumerable<ProviderConfig>>(sp =>
        {
            ProviderConfigLoader loader = sp.GetRequiredService<ProviderConfigLoader>();
            return loader.Load();
        });

        services.AddSingleton<ICredentialFileStore, CredentialFileStore>();
        services.AddSingleton<ICredentialResolver, CredentialResolver>();
        services.AddSingleton<IProviderFactory, OpenAiProviderFactory>();
        services.AddSingleton<IProviderFactory, AnthropicProviderFactory>();
        services.AddSingleton<IProviderFactory, GeminiProviderFactory>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

        return services;
    }

    /// <summary>
    /// Registers authentication services: credential file store and the
    /// auth command handler.
    /// </summary>
    public static IServiceCollection AddDmonAuth(this IServiceCollection services)
    {
        // ICredentialFileStore is registered in AddDmonProviders — only
        // add it here if providers haven't been registered yet.
        services.TryAddSingleton<ICredentialFileStore, CredentialFileStore>();
        services.AddSingleton<IAuthService, AuthService>();

        return services;
    }

    /// <summary>
    /// Registers extension loading services: tool registry, loaders, extension service,
    /// and promote service.
    /// </summary>
    public static IServiceCollection AddDmonExtensions(this IServiceCollection services)
    {
        services.AddSingleton<IGhCliService, GhCliService>();

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
    /// Call after AddDmonProviders, AddDmonAuth, and AddDmonExtensions.
    /// </summary>
    public static IServiceCollection AddDmonCore(this IServiceCollection services)
    {
        services.AddSingleton<ISessionDirectoryResolver, SessionDirectoryResolver>();
        services.AddSingleton<ISessionStore, SessionStore>();
        services.AddSingleton<IAttachmentStore, AttachmentStore>();

        // Permission runtime dependencies
        services.AddSingleton<IPermissionSettings>(_ =>
            PermissionSettingsLoader.LoadProject(Directory.GetCurrentDirectory()));
        services.AddSingleton<IPermissionPolicy>(sp =>
        {
            IPermissionSettings project = sp.GetRequiredService<IPermissionSettings>();
            return new PermissionPolicy(project, null);
        });

        services.AddSingleton<IEventEmitter>(_ => new EventEmitter(Console.Out));

        services.AddSingleton<AgentConfigResolver>();
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();

        services.AddSingleton<TurnHandler>();
        services.AddSingleton<ITurnHandler>(sp => sp.GetRequiredService<TurnHandler>());

        services.AddSingleton<IModelHandler, NullModelHandler>();
        services.AddSingleton<SessionHandler>();
        services.AddSingleton<ISessionHandler>(sp => sp.GetRequiredService<SessionHandler>());
        services.AddSingleton<IExtensionHandler, NullExtensionHandler>();
        services.AddSingleton<IAuthHandler, NullAuthHandler>();
        services.AddSingleton<ThinkingHandler>();
        services.AddSingleton<IThinkingHandler>(sp => sp.GetRequiredService<ThinkingHandler>());
        services.AddSingleton<ProviderSetupHandler>();
        services.AddSingleton<IProviderSetupHandler>(sp => sp.GetRequiredService<ProviderSetupHandler>());

        services.AddSingleton<CommandDispatcher>();
        services.AddSingleton<BootstrapService>();
        services.AddSingleton<SetupCheckService>();

        services.AddHttpClient();
        services.AddHostedService<BuiltinToolsInitializer>();

        return services;
    }
}
