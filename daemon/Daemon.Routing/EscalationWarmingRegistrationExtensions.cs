using Daemon.Routing;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dmon.Hosting;

/// <summary>
/// Composition verbs that wire escalation warming into the dmon builder.
/// </summary>
public static class EscalationWarmingRegistrationExtensions
{
    /// <summary>
    /// Registers <see cref="EscalationWarmingService"/> to warm the escalation runtime
    /// on session activity and tear it down after an idle window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The warming service is provider-agnostic: it depends only on
    /// <see cref="IProviderExtension"/> (not on any concrete MLX type). The
    /// keyed escalation runtime is supplied by the caller via <paramref name="escalationResolver"/>
    /// so that <c>Daemon.Routing</c> does not take a reference to <c>Dmon.Providers.Mlx</c>.
    /// </para>
    /// <para>
    /// Warming never blocks a turn; teardown never raises a permission prompt (ADR-034 D2).
    /// </para>
    /// </remarks>
    /// <param name="b">The host builder.</param>
    /// <param name="escalationResolver">
    /// Delegate that resolves the escalation <see cref="IProviderExtension"/> from the
    /// service provider. Invoked once at singleton construction time.
    /// </param>
    /// <param name="options">Optional idle-timeout options; defaults to a 10-minute idle window.</param>
    /// <returns><paramref name="b"/>, for fluent chaining.</returns>
    public static IDmonHostBuilder AddEscalationWarming(
        this IDmonHostBuilder b,
        Func<IServiceProvider, IProviderExtension> escalationResolver,
        EscalationWarmingOptions? options = null)
    {
        b.Services.AddSingleton(sp => new EscalationWarmingService(
            escalationResolver(sp),
            options ?? new EscalationWarmingOptions(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILogger<EscalationWarmingService>>()));
        b.Services.AddSingleton<ISessionActivityListener>(
            sp => sp.GetRequiredService<EscalationWarmingService>());
        return b;
    }
}
