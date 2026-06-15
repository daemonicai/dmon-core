using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the Anthropic (Claude) cloud provider.
/// </summary>
public static class UseAnthropicExtensions
{
    /// <summary>
    /// Registers the Anthropic provider factory via DI-discovery.
    /// </summary>
    public static T UseAnthropic<T>(this T registration)
        where T : IProviderRegistration
    {
        registration.Services.AddSingleton<IProviderFactory>(new Dmon.Providers.Anthropic.AnthropicProviderFactory());
        return registration;
    }

    /// <summary>
    /// Registers the Anthropic provider factory and sets the active model.
    /// <paramref name="model"/> is the bare model ID (e.g. "claude-sonnet-4-6");
    /// the prefix "anthropic" is the factory's <see cref="IProviderFactory.AdapterName"/>.
    /// </summary>
    public static T UseAnthropic<T>(this T registration, string model)
        where T : IProviderRegistration
        => registration.UseAnthropic().UseModel("anthropic", model);
}
