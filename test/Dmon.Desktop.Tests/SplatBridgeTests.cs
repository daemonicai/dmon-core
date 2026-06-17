using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;
using Splat;
using Splat.Microsoft.Extensions.DependencyInjection;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Proves that view resolution for <see cref="IViewFor{TViewModel}"/> goes through the
/// Microsoft.Extensions.DependencyInjection container via the Splat bridge, not through
/// convention-only assembly scanning. This is the same resolution path used by
/// <see cref="RoutedViewHost"/> at runtime (spec: "View resolution flows through MS DI").
/// </summary>
public sealed class SplatBridgeTests : IClassFixture<ReactiveUiTestFixture>, IDisposable
{
    // Capture the resolver active before the test so we can restore it afterward.
    // This keeps Splat's process-global state from leaking between tests.
    private readonly IReadonlyDependencyResolver _prior;

    public SplatBridgeTests()
    {
        _prior = Locator.Current;
    }

    public void Dispose()
    {
        if (_prior is IDependencyResolver mutable)
        {
            Locator.SetLocator(mutable);
        }
    }

    [Fact]
    public void ViewForViewModel_ResolvesFromMediContainer_NotFromScan()
    {
        // Arrange — build a MEDI container with one explicit IViewFor registration.
        ServiceCollection services = new();
        services.UseMicrosoftDependencyResolver();
        services.AddTransient<IViewFor<StubViewModel>, StubView>();

        IServiceProvider provider = services.BuildServiceProvider();
        provider.UseMicrosoftDependencyResolver();

        // Act — resolve via Locator.Current.GetService, which is the exact internal call
        // DefaultViewLocator.ResolveView makes before RoutedViewHost renders the view.
        // ViewLocator.Current itself requires Avalonia platform init (headless runtime),
        // so we test at the mechanism level: that Locator.Current (post-bridge) returns
        // the MEDI-registered instance rather than a convention-scan result.
        IViewFor<StubViewModel>? view = Locator.Current.GetService<IViewFor<StubViewModel>>();

        // Assert — we get an instance and it came from MEDI (it's our registered type).
        Assert.NotNull(view);
        Assert.IsType<StubView>(view);
    }

    [Fact]
    public void ViewForUnregisteredViewModel_ReturnsNull_NotScanResult()
    {
        // Proves that without an explicit registration nothing leaks in via scanning.
        ServiceCollection services = new();
        services.UseMicrosoftDependencyResolver();

        IServiceProvider provider = services.BuildServiceProvider();
        provider.UseMicrosoftDependencyResolver();

        IViewFor<StubViewModel>? view = Locator.Current.GetService<IViewFor<StubViewModel>>();

        Assert.Null(view);
    }

    [Fact]
    public void AddDesktopServices_RegistersWithoutThrowing()
    {
        // Proves the composition root extension is callable without Avalonia running.
        ServiceCollection services = new();
        Exception? caught = Record.Exception(() => services.AddDesktopServices());
        Assert.Null(caught);
    }

    [Fact]
    public void BuildDesktopServiceProvider_RegistersReactiveUIPlatformServices()
    {
        // Regression test for the bridge bug: after UseMicrosoftDependencyResolver() swaps
        // the Splat resolver, ICreatesObservableForProperty (and other RxUI platform services)
        // must be re-populated into the new resolver via InitializeSplat() and the
        // ReactiveUIBuilder (WithCoreServices/WithPlatformServices/BuildApp).
        // Without the fix, this assert throws "Could not find ICreatesObservableForProperty".
        CompositionRoot.BuildDesktopServiceProvider();

        ICreatesObservableForProperty? service =
            Locator.Current.GetService<ICreatesObservableForProperty>();

        Assert.NotNull(service);
    }

    [Fact]
    public void BuildDesktopServiceProvider_WhenAnyValue_DoesNotThrow()
    {
        // Proves the fix end-to-end: a ReactiveObject that uses WhenAnyValue (the exact
        // call site that threw "Could not find ICreatesObservableForProperty" at runtime)
        // can be constructed and subscribed to without throwing after the bridge runs.
        CompositionRoot.BuildDesktopServiceProvider();

        Exception? caught = Record.Exception(() =>
        {
            BridgeProbeViewModel vm = new();
            bool value = false;
            vm.WhenAnyValue(x => x.IsActive).Subscribe(v => value = v);
            vm.IsActive = true;
            Assert.True(value);
        });

        Assert.Null(caught);
    }
}

// Minimal fixture type for probing WhenAnyValue through the bridged resolver.
internal sealed class BridgeProbeViewModel : ReactiveObject
{
    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => this.RaiseAndSetIfChanged(ref _isActive, value);
    }
}

// Minimal fixture types — no IScreen dependency; purely for proving the bridge.
internal sealed class StubViewModel : ReactiveObject { }

internal sealed class StubView : IViewFor<StubViewModel>
{
    public StubViewModel? ViewModel { get; set; }
    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value as StubViewModel;
    }
}
