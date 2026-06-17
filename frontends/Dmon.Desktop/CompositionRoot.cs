using Dmon.Desktop.Views;
using Dmon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace Dmon.Desktop;

/// <summary>
/// Registers the desktop app's services. Callable without launching the GUI — every
/// registration here is inspectable and unit-testable independently of Avalonia.
/// </summary>
public static class CompositionRoot
{
    /// <summary>
    /// Adds core desktop services to the container. Views are registered explicitly as
    /// <see cref="IViewFor{TViewModel}"/> — no convention-based assembly scanning.
    /// Add further services/view-models here as later groups implement them.
    /// </summary>
    /// <param name="services">The service collection to populate.</param>
    /// <param name="corePathOverride">Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.</param>
    public static IServiceCollection AddDesktopServices(
        this IServiceCollection services,
        string? corePathOverride = null)
    {
        // ICoreLauncher — testable seam; real launcher in production.
        services.AddSingleton<ICoreLauncher>(_ => new CoreLauncher());

        // CoreSessionService — singleton for the app lifetime.
        // Production scheduler is RxSchedulers.MainThreadScheduler (the Avalonia dispatcher).
        services.AddSingleton(sp => new CoreSessionService(
            sp.GetRequiredService<ICoreLauncher>(),
            RxSchedulers.MainThreadScheduler,
            corePathOverride));

        // SessionViewModel — singleton for the app lifetime; owns the RoutingState.
        services.AddSingleton<SessionViewModel>();

        // Views are registered explicitly in AddDesktopViews; this extension is the
        // single entry point so callers (App + tests) both use the same root.
        services.AddDesktopViews();
        return services;
    }

    /// <summary>
    /// Registers all <see cref="IViewFor{TViewModel}"/> views. Explicit, not by scan —
    /// the composition root remains fully inspectable (ADR-022 / Decision 7).
    /// </summary>
    public static IServiceCollection AddDesktopViews(this IServiceCollection services)
    {
        // ConversationView — transient; RoutedViewHost creates a new instance per navigation.
        services.AddTransient<IViewFor<ConversationViewModel>, ConversationView>();
        return services;
    }
}
