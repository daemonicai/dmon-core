using Avalonia;
using ReactiveUI.Avalonia;

namespace Dmon.Desktop;

internal sealed class Program
{
    // AppBuilder must be constructed on the UI thread (enforced by [STAThread]).
    // UseReactiveUI registers ReactiveUI platform services (schedulers, activation hooks)
    // into Splat. App.OnFrameworkInitializationCompleted then applies the MEDI bridge so
    // Locator.Current resolves from the Microsoft.Extensions.DependencyInjection container.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .LogToTrace();
}
