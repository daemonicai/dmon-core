using Avalonia;
using ReactiveUI.Avalonia;

namespace Dmon.Desktop;

internal sealed class Program
{
    // AppBuilder must be constructed on the UI thread (enforced by [STAThread]).
    // All Avalonia init stays here; App.OnFrameworkInitializationCompleted wires DI.
    // DI bridge (UseReactiveUIWithMicrosoftDependencyResolver) is wired in Group 2.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .LogToTrace();
}
