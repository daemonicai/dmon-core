using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Providers.Mtplx;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the MTPLX Apple-Silicon local provider.
/// </summary>
public static class UseMtplxExtensions
{
    /// <summary>
    /// Registers the MTPLX provider extension and sets the active model to
    /// <c>mtplx/&lt;model&gt;</c>.
    /// </summary>
    public static T UseMtplx<T>(this T registration, string model)
        where T : IProviderRegistration
    {
        MtplxOptions options = new() { ModelId = model };
        return registration.AddProvider(new MtplxProviderExtension(options))
                           .UseModel("mtplx", model);
    }

    /// <summary>
    /// Registers the MTPLX provider extension using a pre-built <see cref="MtplxOptions"/>.
    /// Use this overload for full control over host, port, timeout, and server path.
    /// <para>
    /// Note: unlike providers with a required model identifier, <see cref="MtplxOptions.ModelId"/>
    /// is nullable — the server reports the active model at runtime when it is unset. When
    /// <see cref="MtplxOptions.ModelId"/> is non-empty this overload calls
    /// <c>UseModel("mtplx", ...)</c> to set a code-level default; when it is <see langword="null"/>
    /// or empty the provider is registered as available but does not force itself as the active
    /// model, leaving config.yaml, MTPLX_MODEL_ID, and the runtime provider-switch RPC to govern
    /// model selection. This prevents a bare <c>.UseMtplx()</c> in a multi-provider default core
    /// from hijacking the active model on platforms where MTPLX is not applicable.
    /// </para>
    /// </summary>
    public static T UseMtplx<T>(this T registration, MtplxOptions options)
        where T : IProviderRegistration
    {
        registration.AddProvider(new MtplxProviderExtension(options));

        if (!string.IsNullOrEmpty(options.ModelId))
            registration.UseModel("mtplx", options.ModelId);

        return registration;
    }

    /// <summary>
    /// Registers the MTPLX provider extension with options sourced from environment variables
    /// (<c>MTPLX_HOST</c>, <c>MTPLX_PORT</c>, <c>MTPLX_MODEL_ID</c>, <c>MTPLX_SERVER_PATH</c>).
    /// The active model default is set only when <c>MTPLX_MODEL_ID</c> is present; otherwise the
    /// server's reported model governs selection at runtime.
    /// </summary>
    public static T UseMtplx<T>(this T registration)
        where T : IProviderRegistration
        => registration.UseMtplx(MtplxOptions.FromEnvironment());
}
