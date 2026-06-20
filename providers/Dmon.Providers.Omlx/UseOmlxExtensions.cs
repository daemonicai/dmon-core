using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Providers.Omlx;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the oMLX Apple-Silicon local provider.
/// </summary>
public static class UseOmlxExtensions
{
    /// <summary>
    /// Registers the oMLX provider extension with options sourced from environment variables
    /// (<c>OMLX_BASE_URL</c>, <c>OMLX_API_KEY</c>).
    /// <para>
    /// This overload is <strong>non-hijacking</strong>: it does not set the active model key,
    /// so a multi-provider default core using <c>.UseOmlx()</c> does not force oMLX as the
    /// active provider when it is not applicable on the current platform.
    /// </para>
    /// </summary>
    public static T UseOmlx<T>(this T registration)
        where T : IProviderRegistration
        => registration.UseOmlx(OmlxConfig.FromEnvironment());

    /// <summary>
    /// Registers the oMLX provider extension with a pre-built <see cref="OmlxConfig"/>.
    /// <para>
    /// This overload is <strong>non-hijacking</strong>: it never calls <c>UseModel</c>,
    /// leaving <c>config.yaml</c>, env vars, and the runtime provider-switch RPC to govern
    /// model selection. This prevents a bare <c>.UseOmlx()</c> from stealing the active model
    /// on platforms where oMLX is not the intended terminal provider.
    /// </para>
    /// </summary>
    public static T UseOmlx<T>(this T registration, OmlxConfig config)
        where T : IProviderRegistration
    {
        registration.AddProvider(new OmlxProviderExtension(config));
        return registration;
    }
}
