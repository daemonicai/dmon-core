using Dmon.Abstractions.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dmon.Memory.Meko;

/// <summary>
/// Extension methods for <see cref="IServiceCollection"/> to register the Meko-backed
/// long-term memory store (3.6).
/// </summary>
public static class MekoMemoryServiceExtensions
{
    private const string DefaultConfigSection = "Meko";

    /// <summary>
    /// Registers <see cref="ILongTermMemory"/> backed by Meko over MCP.
    /// <para>
    /// When <see cref="MekoLongTermOptions.ApiKey"/> is empty the disabled (no-op) store
    /// is registered instead — fail-fast with a clear log rather than sending an empty
    /// <c>Bearer</c> header (section-2 reviewer nit, D8).
    /// </para>
    /// No MCP connection is established at registration time (lazy init, D12).
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configuration">
    /// Configuration used to bind <see cref="MekoLongTermOptions"/> from the
    /// <paramref name="configSection"/> key.
    /// </param>
    /// <param name="configSection">
    /// The configuration section key. Defaults to <c>"Meko"</c>.
    /// </param>
    public static IServiceCollection AddMekoLongTermMemory(
        this IServiceCollection services,
        IConfiguration configuration,
        string configSection = DefaultConfigSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MekoLongTermOptions>()
            .Bind(configuration.GetSection(configSection));

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MekoLongTermOptions>>().Value;
            return options;
        });

        services.AddSingleton<IMekoToolInvoker>(sp =>
        {
            var options = sp.GetRequiredService<MekoLongTermOptions>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            return new MekoToolInvoker(options, loggerFactory);
        });

        services.AddSingleton<MekoMemoryContext>(sp =>
        {
            var options = sp.GetRequiredService<MekoLongTermOptions>();
            return new MekoMemoryContext(options);
        });

        services.AddSingleton<ILongTermMemory>(sp =>
        {
            var options = sp.GetRequiredService<MekoLongTermOptions>();

            if (string.IsNullOrWhiteSpace(options.ApiKey))
            {
                var warnLogger = sp.GetService<ILogger<MekoLongTermOptions>>();
                warnLogger?.LogWarning(
                    "Meko ApiKey is empty — long-term memory is disabled. " +
                    "Set '{Section}:ApiKey' (mko_tkn_…) in configuration to enable it.",
                    configSection);
                return DisabledLongTermMemory.Instance;
            }

            var invoker = sp.GetRequiredService<IMekoToolInvoker>();
            var context = sp.GetRequiredService<MekoMemoryContext>();
            var logger = sp.GetRequiredService<ILogger<MekoLongTermMemory>>();
            return new MekoLongTermMemory(invoker, context, options, logger);
        });

        return services;
    }
}
