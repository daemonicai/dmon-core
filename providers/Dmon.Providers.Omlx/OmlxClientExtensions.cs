using Dmon.Abstractions.Providers;
using Dmon.Providers.Omlx;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Per-model client construction helper for the oMLX provider.
/// </summary>
public static class OmlxClientExtensions
{
    /// <summary>
    /// Resolves the registered <see cref="OmlxProviderExtension"/> from DI, ensures oMLX is
    /// running via <see cref="OmlxProviderExtension.EnsureRunningAsync"/>, then constructs and
    /// returns an <see cref="IChatClient"/> for the specified <paramref name="model"/>.
    /// </summary>
    /// <remarks>
    /// Each call builds a fresh <see cref="IChatClient"/> keyed to <paramref name="model"/>,
    /// so two calls with different model ids return two independent clients over the same
    /// oMLX base URL.  EnsureRunningAsync is called before each construction — it is a no-op
    /// when the server is already up.
    /// </remarks>
    public static async ValueTask<IChatClient> OmlxClient(
        this IServiceProvider sp,
        string model,
        CancellationToken cancellationToken = default)
    {
        OmlxProviderExtension ext = sp.GetServices<IProviderExtension>()
            .OfType<OmlxProviderExtension>()
            .First();

        await ext.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        ProviderConfig modelCfg = new()
        {
            Name = "oMLX",
            Adapter = "omlx",
            // Leave BaseUrl null so the factory falls back to the env-resolved OmlxConfig.BaseUrl.
            BaseUrl = null,
            DefaultModelId = model,
            Auth = new ProviderAuthConfig { Type = "apiKey", EnvVar = "OMLX_API_KEY" },
        };

        return await ((OmlxProviderFactory)ext.CreateFactory())
            .CreateAsync(modelCfg, apiKey: null, cancellationToken)
            .ConfigureAwait(false);
    }
}
