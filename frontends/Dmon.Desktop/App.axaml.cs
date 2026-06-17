using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Splat.Microsoft.Extensions.DependencyInjection;

namespace Dmon.Desktop;

public sealed partial class App : Application
{
    private readonly string? _corePathOverride;

    // Owned for the application lifetime; cancelled on Exit to unblock the core pump.
    private readonly CancellationTokenSource _appCts = new();

    public App() { }

    public App(string? corePathOverride)
    {
        _corePathOverride = corePathOverride;
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Step 1: swap Splat's global resolver to a collection-backed one.
        // UseReactiveUI (Program.cs) already registered ReactiveUI platform services
        // into the default resolver; ReactiveUI re-resolves most of them lazily via
        // RxSchedulers, so they remain available after the resolver is replaced below.
        ServiceCollection services = new();
        services.UseMicrosoftDependencyResolver();

        // Step 2: register app services and explicit IViewFor<TViewModel> views.
        services.AddDesktopServices(_corePathOverride);

        // Step 3: build the provider and make it the active Splat resolver.
        // From this point, Locator.Current resolves from the MEDI container.
        IServiceProvider provider = services.BuildServiceProvider();
        provider.UseMicrosoftDependencyResolver();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CoreSessionService sessionService = provider.GetRequiredService<CoreSessionService>();
            desktop.MainWindow = new MainWindow(sessionService);

            // Teardown: cancel on Exit so the core pump unblocks before process exit.
            desktop.Exit += async (_, _) =>
            {
                await _appCts.CancelAsync().ConfigureAwait(false);
                await sessionService.DisposeAsync().ConfigureAwait(false);
            };

            // Start the core in the background; MainWindow observes State reactively.
            _ = sessionService.StartAsync(_appCts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
