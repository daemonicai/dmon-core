using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Splat.Microsoft.Extensions.DependencyInjection;

namespace Dmon.Desktop;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Step 1: swap Splat's global resolver to a collection-backed one.
        // UseReactiveUI (Program.cs) already registered ReactiveUI platform services
        // into the default resolver; ReactiveUI re-resolves most of them lazily via
        // RxApp, so they remain available after the resolver is replaced below.
        ServiceCollection services = new();
        services.UseMicrosoftDependencyResolver();

        // Step 2: register app services and explicit IViewFor<TViewModel> views.
        services.AddDesktopServices();

        // Step 3: build the provider and make it the active Splat resolver.
        // From this point, Locator.Current resolves from the MEDI container.
        IServiceProvider provider = services.BuildServiceProvider();
        provider.UseMicrosoftDependencyResolver();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
