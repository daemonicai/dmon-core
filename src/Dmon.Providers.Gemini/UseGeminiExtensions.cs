using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the Google Gemini cloud provider.
/// </summary>
public static class UseGeminiExtensions
{
    /// <summary>
    /// Registers the Gemini provider factory via DI-discovery.
    /// </summary>
    public static T UseGemini<T>(this T registration)
        where T : IProviderRegistration
    {
        registration.Services.AddSingleton<IProviderFactory>(new Dmon.Providers.Gemini.GeminiProviderFactory());
        return registration;
    }

    /// <summary>
    /// Registers the Gemini provider factory and sets the active model.
    /// <paramref name="model"/> is the bare model ID (e.g. "gemini-2.5-pro");
    /// the prefix "gemini" is the factory's <see cref="IProviderFactory.AdapterName"/>.
    /// </summary>
    public static T UseGemini<T>(this T registration, string model)
        where T : IProviderRegistration
        => registration.UseGemini().UseModel("gemini", model);
}
