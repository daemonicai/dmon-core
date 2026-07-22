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
    /// Resolves the registered <see cref="MlxProviderExtension"/> for <paramref name="key"/>
    /// and returns the factory-produced, self-healing <see cref="IChatClient"/> bound to that runtime.
    /// </summary>
    /// <remarks>
    /// Suitable as the factory delegate for <c>UseTriage</c> / <c>AddEscalation</c> in
    /// <c>Daemon.Routing</c>: <c>sp => sp.MlxClient(MlxRuntimeKeys.Firstline)</c>.
    /// The self-heal is single-sourced in <see cref="MlxProviderFactory.CreateAsync"/>: it awaits
    /// the extension's <c>EnsureRunningAsync</c> at construction and wraps its output in an
    /// attach-first <see cref="EnsureRunningChatClient"/>. This helper adds no duplicate
    /// ensure-running call and no duplicate wrapper — it returns the factory client verbatim.
    /// </remarks>
    public static ValueTask<IChatClient> MlxClient(
        this IServiceProvider sp,
        string key,
        CancellationToken cancellationToken = default)
    {
        MlxProviderExtension ext = sp.GetRequiredKeyedService<MlxProviderExtension>(key);

        ProviderConfig modelCfg = new()
        {
            Name = "mlx",
            Adapter = "mlx",
            // Leave BaseUrl null so the factory falls back to MlxRuntimeState.BaseUrl,
            // which its own EnsureRunningAsync seeds before reading it.
            BaseUrl = null,
            DefaultModelId = ext.Options.ModelId,
            Auth = new ProviderAuthConfig { Type = "none" },
        };

        return ((MlxProviderFactory)ext.CreateFactory())
            .CreateAsync(modelCfg, apiKey: null, cancellationToken);
    }
}
