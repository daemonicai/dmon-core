using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

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
        // Build the service provider via the full bridge sequence (see CompositionRoot).
        // This re-registers ReactiveUI platform services (ICreatesObservableForProperty, etc.)
        // into the MEDI-backed resolver so WhenAnyValue/ObservableForProperty work after the
        // Splat resolver is replaced. Program.cs UseReactiveUI still runs first to configure
        // schedulers/activation on the Avalonia dispatcher; the ReactiveUIBuilder
        // (WithCoreServices/WithPlatformServices/BuildApp) called inside BuildDesktopServiceProvider
        // then repopulates the new resolver with the platform service registrations.
        IServiceProvider provider = CompositionRoot.BuildDesktopServiceProvider(_corePathOverride);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            CoreSessionService sessionService = provider.GetRequiredService<CoreSessionService>();
            SessionViewModel sessionViewModel = provider.GetRequiredService<SessionViewModel>();
            desktop.MainWindow = new MainWindow(sessionService, sessionViewModel);

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
