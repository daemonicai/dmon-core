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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Dmon.Core;

public static class DmonServiceExtensions
{
    /// <summary>
    /// Registers authentication services: credential file store and the
    /// auth command handler.
    /// </summary>
    public static IServiceCollection AddDmonAuth(this IServiceCollection services)
    {
        // ICredentialFileStore is registered by AddDmonCore — only
        // add it here if core services haven't been registered yet.
        services.TryAddSingleton<ICredentialFileStore, CredentialFileStore>();
        services.AddSingleton<IAuthService, AuthService>();

        return services;
    }

    /// <summary>
    /// Registers extension services: tool registry, middleware registry, pipeline builder,
    /// and security helpers. Extensions themselves are registered through the
    /// <c>DmonHostBuilder</c> at composition time.
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

        services.AddSingleton<ProfilesConfigReader>();
        services.AddSingleton<EffectiveProfileSetResolver>();

        return services;
    }

    /// <summary>
    /// Registers the RPC infrastructure: event emitter, command dispatcher,
    /// turn handler, stub handlers, and bootstrap service.
    /// Also registers provider infrastructure (config loading, credential resolution,
    /// and the provider registry) — provider factories themselves are composed via
    /// Use&lt;Provider&gt;() verbs in the Dmon.cs composition root.
    /// Call after AddDmonAuth and AddDmonExtensions.
    /// </summary>
    public static IServiceCollection AddDmonCore(this IServiceCollection services)
    {
        // Provider infrastructure: config loading, credential resolution, registry.
        // Provider factories are NOT registered here — they come from Use<Provider>()
        // verbs in the composition root (DI-discovered as IProviderFactory singletons).
        services.AddSingleton<ProviderConfigLoader>();
        services.AddSingleton<IEnumerable<ProviderConfig>>(sp =>
        {
            ProviderConfigLoader loader = sp.GetRequiredService<ProviderConfigLoader>();
            return loader.Load();
        });

        services.AddSingleton<ICredentialFileStore, CredentialFileStore>();
        services.AddSingleton<ICredentialResolver, CredentialResolver>();
        services.AddSingleton<IProviderRegistry, ProviderRegistry>();

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

        // TryAdd so DmonHostBuilder's pre-registered TextWriter (injected via WithStdio) wins.
        services.TryAddSingleton<TextWriter>(_ => Console.Out);
        services.TryAddSingleton<TextReader>(_ => Console.In);
        services.TryAddSingleton<IEventEmitter>(sp => new EventEmitter(sp.GetRequiredService<TextWriter>()));

        services.AddSingleton<AgentConfigResolver>();
        // TryAdd so a composition root may supply its own ISystemPromptBuilder via
        // Services.AddSingleton<ISystemPromptBuilder>(…) and have it win.
        services.TryAddSingleton<ISystemPromptBuilder, SystemPromptBuilder>();

        // Agent profile resolution — resolved once per session, shared by Groups 4-6 consumers.
        // Config paths mirror BootstrapService conventions.
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
