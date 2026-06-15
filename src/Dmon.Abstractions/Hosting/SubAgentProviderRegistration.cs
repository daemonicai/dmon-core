using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// An isolated <see cref="IProviderRegistration"/> backed by its own
/// <see cref="IServiceCollection"/> and <see cref="IConfigurationManager"/>.
/// Never touches the host's DI container or <c>IProviderRegistry</c>.
/// </summary>
/// <remarks>
/// Call <see cref="Build"/> after the author's configuration action has run to obtain a
/// validated <see cref="SubAgentChatClientFactory"/>.  Structural validation (exactly one
/// provider verb + a model selected) happens eagerly at <see cref="Build"/>; credential
/// resolution and client construction are deferred to <see cref="SubAgentChatClientFactory.CreateAsync"/>.
/// </remarks>
internal sealed class SubAgentProviderRegistration : IProviderRegistration
{
    private readonly ServiceCollection _services = new();
    private readonly ConfigurationManager _configuration = new();

    public IServiceCollection Services => _services;
    public IConfigurationManager Configuration => _configuration;

    /// <summary>
    /// Validates the registration structurally and returns the factory.
    /// Throws <see cref="InvalidOperationException"/> when:
    /// <list type="bullet">
    ///   <item>no provider verb was called (zero providers registered)</item>
    ///   <item>more than one provider verb was called</item>
    ///   <item>no model was selected via <c>UseModel</c></item>
    /// </list>
    /// </summary>
    public SubAgentChatClientFactory Build()
    {
        ServiceProvider serviceProvider = _services.BuildServiceProvider();

        IProviderFactory[] factories = serviceProvider.GetServices<IProviderFactory>().ToArray();
        IProviderExtension[] extensions = serviceProvider.GetServices<IProviderExtension>().ToArray();

        int totalProviders = factories.Length + extensions.Length;

        if (totalProviders == 0)
        {
            throw new InvalidOperationException(
                "Sub-agent registration requires exactly one provider verb (e.g. UseGemini, UseOpenAI). No provider was registered.");
        }

        if (totalProviders > 1)
        {
            throw new InvalidOperationException(
                $"Sub-agent registration requires exactly one provider verb. {totalProviders} were registered. Call exactly one Use<Provider> verb.");
        }

        string? activeModel = _configuration[ConfigurationKeys.ActiveModel];
        if (string.IsNullOrWhiteSpace(activeModel))
        {
            throw new InvalidOperationException(
                "Sub-agent registration requires a model to be selected. Call UseModel or use the model-accepting overload of the provider verb (e.g. UseGemini(\"flash\")).");
        }

        // Resolve the factory: either directly registered or via IProviderExtension.CreateFactory().
        IProviderFactory resolvedFactory = factories.Length == 1
            ? factories[0]
            : extensions[0].CreateFactory();

        return new SubAgentChatClientFactory(resolvedFactory, activeModel);
    }
}
