using Dmon.Abstractions;
using Dmon.Abstractions.Memory;
using Dmon.Abstractions.Profiles;
using Dmon.Abstractions.Providers;
using Dmon.Core.Auth;
using Dmon.Core.Config;
using Dmon.Core.Extensions;
using Dmon.Core.Extensions.Security;
using Dmon.Core.GitHub;
using Dmon.Core.SystemPrompt;
using Dmon.Core.Bootstrap;
using Dmon.Core.Permissions;
using Dmon.Core.Profiles;
using Dmon.Protocol.Permissions;
using Dmon.Core.Providers;
using Dmon.Core.Rpc;
using Dmon.Core.Session;
using Dmon.Providers;
using Dmon.Providers.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<IProviderFactory, OllamaProviderFactory>();
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

        services.AddSingleton<IExtensionSourceFetcher>(sp =>
            new ExtensionSourceFetcher(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
                sp.GetRequiredService<IGhCliService>()));
        services.AddSingleton<IExtensionSecurityAnalyser>(sp =>
            new ExtensionSecurityAnalyser(sp.GetRequiredService<IProviderRegistry>()));

        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddSingleton<IMiddlewareRegistry, MiddlewareRegistry>();
        services.AddSingleton<MiddlewarePipelineBuilder>();
        services.AddSingleton<CsxScriptLoader>();
        services.AddSingleton<NuGetExtensionLoader>();
        services.AddSingleton<IExtensionLoader>(sp => sp.GetRequiredService<CsxScriptLoader>());
        services.AddSingleton<IExtensionLoader>(sp => sp.GetRequiredService<NuGetExtensionLoader>());
        services.AddSingleton<ExtensionService>(sp => new ExtensionService(
            sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<IEnumerable<IExtensionLoader>>(),
            sp.GetRequiredService<ILogger<ExtensionService>>(),
            sp.GetService<IProviderRegistry>(),
            sp.GetRequiredService<IMiddlewareRegistry>()));
        services.AddSingleton<PromoteService>();

        services.AddSingleton<ExtensionsConfigReader>();
        services.AddSingleton<EffectiveExtensionSetResolver>();
        services.AddSingleton<StartupExtensionLoader>();

        services.AddSingleton<ProfilesConfigReader>();
        services.AddSingleton<EffectiveProfileSetResolver>();

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
        services.AddSingleton<IAttachmentStore, AttachmentStore>();

        // Lazy<IMemory?> breaks the construction cycle: SessionStore → IMemory → IShortTermMemory → ISessionStore.
        // The Lazy defers IMemory resolution to after the DI container is fully built.
        services.AddSingleton(sp => new Lazy<IMemory?>(() => sp.GetService<IMemory>()));
        services.AddSingleton<ISessionStore>(sp => new SessionStore(
            sp.GetRequiredService<ISessionDirectoryResolver>(),
            sp.GetRequiredService<IAttachmentStore>(),
            sp.GetRequiredService<ILogger<SessionStore>>(),
            sp.GetRequiredService<ILoggerFactory>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<Lazy<IMemory?>>()));

        // Permission runtime dependencies
        services.AddSingleton<IPermissionSettings>(_ =>
            PermissionSettingsLoader.LoadProject(Directory.GetCurrentDirectory()));
        services.AddSingleton<IActiveModelStore>(sp =>
            new ActiveModelStore(
                sp.GetRequiredService<IConfiguration>(),
                Directory.GetCurrentDirectory()));
        services.AddSingleton<IPermissionPolicy>(sp =>
        {
            IPermissionSettings project = sp.GetRequiredService<IPermissionSettings>();
            return new PermissionPolicy(project, null);
        });

        services.AddSingleton<IEventEmitter>(_ => new EventEmitter(Console.Out));

        services.AddSingleton<AgentConfigResolver>();
        services.AddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();

        // Agent profile resolution — resolved once per session, shared by Groups 4-6 consumers.
        // Config paths mirror StartupExtensionLoader / BootstrapService conventions.
        services.AddSingleton<IAgentProfileResolver>(sp =>
        {
            EffectiveProfileSetResolver setResolver = sp.GetRequiredService<EffectiveProfileSetResolver>();
            string userConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dmon", "config.yaml");
            string projectConfigPath = Path.Combine(
                Directory.GetCurrentDirectory(),
                ".dmon", "config.yaml");
            return new AgentProfileResolver(setResolver, userConfigPath, projectConfigPath);
        });
        services.AddSingleton<AgentProfileContext>();
        services.AddSingleton<ISessionAssetProvisioner>(
            _ => new SessionAssetProvisioner(Directory.GetCurrentDirectory()));

        services.AddSingleton<TurnHandler>();
        services.AddSingleton<ITurnHandler>(sp => sp.GetRequiredService<TurnHandler>());

        services.AddSingleton<ModelListHandler>();
        services.AddSingleton<ModelModelsHandler>();
        services.AddSingleton<IModelHandler, NullModelHandler>();
        services.AddSingleton<SessionHandler>();
        services.AddSingleton<ISessionHandler>(sp => sp.GetRequiredService<SessionHandler>());
        services.AddSingleton<ConfigExtensionHandler>();
        services.AddSingleton<IExtensionHandler>(sp => sp.GetRequiredService<ConfigExtensionHandler>());
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

        // Expose IConfigurationRoot so middleware can call
        // GetRequiredService<IConfigurationRoot>().GetSection("middleware:<ClassName>").
        // The host IConfiguration built by HostApplicationBuilder is always an
        // IConfigurationRoot; the cast is safe here by construction.
        services.TryAddSingleton<IConfigurationRoot>(sp =>
            (IConfigurationRoot)sp.GetRequiredService<IConfiguration>());

        return services;
    }
}
