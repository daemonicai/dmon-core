using System.Reactive.Concurrency;
using ReactiveUI.Builder;

namespace Dmon.Desktop.Tests;

/// <summary>
/// xUnit class fixture that initialises ReactiveUI for headless unit tests.
/// Uses <see cref="RxAppBuilder"/> to register <see cref="ImmediateScheduler.Instance"/>
/// as the main-thread scheduler so VM construction succeeds without Avalonia or a real UI thread.
///
/// Usage: add <c>IClassFixture&lt;ReactiveUiTestFixture&gt;</c> to any test class that
/// constructs <see cref="ReactiveUI.ReactiveObject"/>-derived view-models.
/// </summary>
public sealed class ReactiveUiTestFixture
{
    public ReactiveUiTestFixture()
    {
        RxAppBuilder
            .CreateReactiveUIBuilder()
            .WithMainThreadScheduler(ImmediateScheduler.Instance)
            .BuildApp();
    }
}
