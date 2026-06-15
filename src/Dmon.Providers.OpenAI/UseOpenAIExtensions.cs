using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the OpenAI cloud provider.
/// </summary>
public static class UseOpenAIExtensions
{
    /// <summary>
    /// Registers the OpenAI provider factory via DI-discovery.
    /// </summary>
    public static T UseOpenAI<T>(this T registration)
        where T : IProviderRegistration
    {
        registration.Services.AddSingleton<IProviderFactory>(new Dmon.Providers.OpenAI.OpenAiProviderFactory());
        return registration;
    }

    /// <summary>
    /// Registers the OpenAI provider factory and sets the active model.
    /// <paramref name="model"/> is the bare model ID (e.g. "gpt-4o");
    /// the prefix "openai" is the factory's <see cref="IProviderFactory.AdapterName"/>.
    /// </summary>
    public static T UseOpenAI<T>(this T registration, string model)
        where T : IProviderRegistration
        => registration.UseOpenAI().UseModel("openai", model);
}
