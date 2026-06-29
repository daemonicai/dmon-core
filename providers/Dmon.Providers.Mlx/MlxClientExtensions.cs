using Dmon.Abstractions.Providers;
using Dmon.Providers.Mlx;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Per-runtime client construction helper for the MLX provider pair.
/// </summary>
public static class MlxClientExtensions
{
    /// <summary>
    /// Resolves the registered <see cref="MlxProviderExtension"/> for <paramref name="key"/>,
    /// ensures the runtime is running via <see cref="MlxProviderExtension.EnsureRunningAsync"/>,
    /// then constructs and returns an <see cref="IChatClient"/> bound to that runtime.
    /// </summary>
    /// <remarks>
    /// Suitable as the factory delegate for <c>UseTriage</c> / <c>AddEscalation</c> in
    /// <c>Daemon.Routing</c>: <c>sp => sp.MlxClient(MlxRuntimeKeys.Firstline)</c>.
    /// EnsureRunningAsync is the self-heal backstop — a no-op when the runtime is already up,
    /// a respawn when it was torn down by the daemon scheduler.
    /// </remarks>
    public static async ValueTask<IChatClient> MlxClient(
        this IServiceProvider sp,
        string key,
        CancellationToken cancellationToken = default)
    {
        MlxProviderExtension ext = sp.GetRequiredKeyedService<MlxProviderExtension>(key);

        await ext.EnsureRunningAsync(cancellationToken).ConfigureAwait(false);

        ProviderConfig modelCfg = new()
        {
            Name = "mlx",
            Adapter = "mlx",
            // Leave BaseUrl null so the factory falls back to MlxRuntimeState.BaseUrl set during EnsureRunningAsync.
            BaseUrl = null,
            DefaultModelId = ext.Options.ModelId,
            Auth = new ProviderAuthConfig { Type = "none" },
        };

        return await ((MlxProviderFactory)ext.CreateFactory())
            .CreateAsync(modelCfg, apiKey: null, cancellationToken)
            .ConfigureAwait(false);
    }
}
