using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Providers.LlamaCpp;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the llama.cpp local provider.
/// </summary>
public static class UseLlamaCppExtensions
{
    /// <summary>
    /// Registers the llama.cpp provider extension and sets the active model.
    /// <paramref name="model"/> is parsed as <c>repo[:quant]</c> — everything before the first
    /// <c>:</c> becomes <see cref="LlamaCppOptions.ModelId"/>;  if a colon is present the
    /// right-hand side overrides the default <see cref="LlamaCppOptions.Quant"/>.
    /// The active model key is set to <c>llamacpp/&lt;modelId&gt;</c>.
    /// </summary>
    public static T UseLlamaCpp<T>(this T registration, string model)
        where T : IProviderRegistration
    {
        int colon = model.IndexOf(':');
        LlamaCppOptions options = colon < 0
            ? new LlamaCppOptions { ModelId = model }
            : new LlamaCppOptions { ModelId = model[..colon], Quant = model[(colon + 1)..] };

        return registration.AddProvider(new LlamaCppProviderExtension(options))
                           .UseModel("llamacpp", options.ModelId);
    }

    /// <summary>
    /// Registers the llama.cpp provider extension using a pre-built <see cref="LlamaCppOptions"/>
    /// and sets the active model to <c>llamacpp/&lt;options.ModelId&gt;</c>.
    /// Use this overload for full control over host, port, context size, GPU layers, etc.
    /// </summary>
    public static T UseLlamaCpp<T>(this T registration, LlamaCppOptions options)
        where T : IProviderRegistration
        => registration.AddProvider(new LlamaCppProviderExtension(options))
                       .UseModel("llamacpp", options.ModelId);
}
