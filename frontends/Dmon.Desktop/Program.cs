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
    public static void Main(string[] args)
    {
        string? corePathOverride = null;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--core-path")
            {
                corePathOverride = args[i + 1];
                break;
            }
        }

        BuildAvaloniaApp(corePathOverride).StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp(string? corePathOverride = null) =>
        AppBuilder.Configure(() => new App(corePathOverride))
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .LogToTrace();
}
