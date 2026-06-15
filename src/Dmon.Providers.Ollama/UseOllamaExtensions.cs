using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the Ollama local provider.
/// </summary>
public static class UseOllamaExtensions
{
    /// <summary>
    /// Registers the Ollama provider extension via DI-discovery.
    /// Uses the default base URL (http://localhost:11434).
    /// </summary>
    public static T UseOllama<T>(this T registration)
        where T : IProviderRegistration
        => registration.AddProvider(new Dmon.Providers.Ollama.OllamaProviderExtension());

    /// <summary>
    /// Registers the Ollama provider extension with a custom base URL via DI-discovery.
    /// </summary>
    public static T UseOllama<T>(this T registration, string baseUrl)
        where T : IProviderRegistration
        => registration.AddProvider(new Dmon.Providers.Ollama.OllamaProviderExtension(baseUrl));

    /// <summary>
    /// Registers the Ollama provider extension and sets the active model.
    /// <paramref name="model"/> is the bare model ID (e.g. "llama3.2");
    /// the prefix "ollama" is the factory's <see cref="IProviderFactory.AdapterName"/>.
    /// </summary>
    public static T UseOllama<T>(this T registration, string baseUrl, string model)
        where T : IProviderRegistration
        => registration.UseOllama(baseUrl).UseModel("ollama", model);
}
