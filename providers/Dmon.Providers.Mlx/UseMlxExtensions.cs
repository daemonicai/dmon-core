using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Providers.Mlx;

namespace Dmon.Hosting;

/// <summary>
/// Composition verb for using a single MLX runtime as the active provider.
/// Distinct from the keyed router backends registered by <c>AddMlxFirstline</c>/
/// <c>AddMlxEscalation</c> (ADR-027): this path makes MLX the terminal
/// <see cref="Dmon.Abstractions.Providers.IProviderExtension"/> for a standalone agent.
/// </summary>
public static class UseMlxExtensions
{
    /// <summary>
    /// Registers a single MLX runtime as the active provider using a pre-built
    /// <see cref="MlxRuntimeOptions"/> and sets the active model to
    /// <c>mlx/&lt;options.ModelId&gt;</c>.
    /// Use this overload for full control over host, port, and ready timeout.
    /// </summary>
    public static T UseMlx<T>(this T registration, MlxRuntimeOptions options)
        where T : IProviderRegistration
        => registration.AddProvider(new MlxProviderExtension(options))
                       .UseModel("mlx", options.ModelId);

    /// <summary>
    /// Registers a single MLX runtime as the active provider for <paramref name="modelId"/>
    /// and sets the active model to <c>mlx/&lt;modelId&gt;</c>.
    /// <para>
    /// <paramref name="modelId"/> must be an explicit <c>mlx-community</c> model id — there is
    /// no chat/coding default, because a standalone MLX agent picks its own model.
    /// </para>
    /// <para>
    /// <paramref name="port"/> defaults to <c>8666</c>, deliberately distinct from the daemon's
    /// fixed firstline (8800) and escalation (8810) runtime ports. MLX is attach-first on a fixed
    /// port, so keeping a standalone <c>UseMlx</c> runtime on its own port avoids colliding with —
    /// or accidentally attaching to — a daemon runtime already listening on 8800/8810.
    /// </para>
    /// </summary>
    public static T UseMlx<T>(this T registration, string modelId, int port = 8666)
        where T : IProviderRegistration
        => registration.UseMlx(new MlxRuntimeOptions { ModelId = modelId, Port = port });
}
