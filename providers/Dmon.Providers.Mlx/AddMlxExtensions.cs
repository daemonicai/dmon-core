using Dmon.Abstractions.Hosting;
using Dmon.Providers.Mlx;
using Microsoft.Extensions.DependencyInjection;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs for the MLX Apple-Silicon local provider pair.
/// </summary>
public static class AddMlxExtensions
{
    /// <summary>
    /// Registers the MLX first-line (E4B) runtime as a keyed singleton resolvable by
    /// <see cref="MlxRuntimeKeys.Firstline"/> for warm/stop and client construction.
    /// Does NOT register as <see cref="Dmon.Abstractions.Providers.IProviderExtension"/> — the
    /// runtimes are router backends, not active-provider candidates (ADR-027).
    /// </summary>
    public static T AddMlxFirstline<T>(this T registration, MlxRuntimeOptions? options = null)
        where T : IProviderRegistration
    {
        MlxRuntimeOptions opts = options ?? MlxRuntimeOptions.Firstline();
        registration.Services.AddKeyedSingleton<MlxProviderExtension>(
            MlxRuntimeKeys.Firstline,
            (_, _) => new MlxProviderExtension(opts));
        return registration;
    }

    /// <summary>
    /// Registers the MLX escalation (26B) runtime as a keyed singleton resolvable by
    /// <see cref="MlxRuntimeKeys.Escalation"/> for warm/stop and client construction.
    /// Does NOT register as <see cref="Dmon.Abstractions.Providers.IProviderExtension"/> — the
    /// runtimes are router backends, not active-provider candidates (ADR-027).
    /// </summary>
    public static T AddMlxEscalation<T>(this T registration, MlxRuntimeOptions? options = null)
        where T : IProviderRegistration
    {
        MlxRuntimeOptions opts = options ?? MlxRuntimeOptions.Escalation();
        registration.Services.AddKeyedSingleton<MlxProviderExtension>(
            MlxRuntimeKeys.Escalation,
            (_, _) => new MlxProviderExtension(opts));
        return registration;
    }
}
