using Dmon.Desktop.Views;
using Dmon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using ReactiveUI.Builder;
using Splat;
using Splat.Microsoft.Extensions.DependencyInjection;

namespace Dmon.Desktop;

/// <summary>
/// Registers the desktop app's services. Callable without launching the GUI — every
/// registration here is inspectable and unit-testable independently of Avalonia.
/// </summary>
public static class CompositionRoot
{
    /// <summary>
    /// Builds and activates the desktop service provider with the full Splat/ReactiveUI
    /// bridge sequence. Must be used instead of calling <c>UseMicrosoftDependencyResolver</c>
    /// + <c>BuildServiceProvider</c> directly, because the bridge sequence must re-register
    /// ReactiveUI platform services (e.g. <c>ICreatesObservableForProperty</c>) into the
    /// new MEDI-backed resolver — they are not preserved when the resolver is replaced.
    /// </summary>
    /// <param name="corePathOverride">Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.</param>
    /// <returns>The built <see cref="IServiceProvider"/>; <c>Locator.Current</c> now resolves from it.</returns>
    public static IServiceProvider BuildDesktopServiceProvider(string? corePathOverride = null)
    {
        ServiceCollection services = new();

        // Step 1: swap Splat's global resolver to a collection-backed one.
        // After this call, Locator.CurrentMutable writes into `services`.
        services.UseMicrosoftDependencyResolver();

        // Step 2: re-register Splat and ReactiveUI platform services into the new resolver.
        // Locator.CurrentMutable now writes into `services`, so InitializeSplat() populates the
        // MEDI collection with Splat core services. The ReactiveUIBuilder (WithCoreServices +
        // WithPlatformServices + BuildApp) then registers ICreatesObservableForProperty and the
        // other RxUI platform services into the same collection. Without this, WhenAnyValue throws
        // "Could not find ICreatesObservableForProperty" when any ReactiveObject is constructed.
        Locator.CurrentMutable.InitializeSplat();
        ReactiveUIBuilder rxBuilder = new(Locator.CurrentMutable, Locator.Current);
        rxBuilder.WithCoreServices();
        rxBuilder.WithPlatformServices();
        rxBuilder.BuildApp();

        // Step 3: register app services and explicit IViewFor<TViewModel> views.
        services.AddDesktopServices(corePathOverride);

        // Step 4: build the provider and make it the active Splat resolver.
        IServiceProvider provider = services.BuildServiceProvider();
        provider.UseMicrosoftDependencyResolver();

        return provider;
    }

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
        // Receives CoreSessionService so it can pass the event stream to ConversationViewModel.
        services.AddSingleton(sp => new SessionViewModel(sp.GetRequiredService<CoreSessionService>()));

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
