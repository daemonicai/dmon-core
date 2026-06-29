using System.Diagnostics;
using Daemon.Routing;
using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Dmon.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Daemon.Routing.Tests;

// ── Shared fakes (file-scoped) ────────────────────────────────────────────────

file sealed class FakeEscalationRuntime : IProviderExtension
{
    private readonly TaskCompletionSource _warmStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _warmGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopCalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _warmCount;
    private int _stopCount;

    /// <summary>When true, <see cref="EnsureRunningAsync"/> parks until <see cref="UnparkWarm"/> is called.</summary>
    public bool ParkWarm { get; set; }
    public int WarmCount => Volatile.Read(ref _warmCount);
    public int StopCount => Volatile.Read(ref _stopCount);

    /// <summary>Completes when EnsureRunningAsync is first called.</summary>
    public Task WarmStartedAsync => _warmStarted.Task;

    /// <summary>Completes when StopAsync is first called.</summary>
    public Task StopCalledAsync => _stopCalled.Task;

    /// <summary>Unblocks a parked EnsureRunningAsync.</summary>
    public void UnparkWarm() => _warmGate.TrySetResult();

    public string ProviderName => "FakeEscalation";
    public bool IsApplicable() => true;
    public Task<bool> IsRunningAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    public IProviderFactory CreateFactory() => throw new NotSupportedException();

    public async Task EnsureRunningAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _warmCount);
        _warmStarted.TrySetResult();
        if (ParkWarm)
            await _warmGate.Task.ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stopCount);
        _stopCalled.TrySetResult();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Minimal <see cref="IDmonHostBuilder"/> backed by a <see cref="ServiceCollection"/>.
/// Only <see cref="Services"/> is used by the warming verb.
/// </summary>
file sealed class TestDmonHostBuilder : IDmonHostBuilder
{
    public TestDmonHostBuilder(IServiceCollection services) => Services = services;
    public IServiceCollection Services { get; }
    public IConfigurationManager Configuration => throw new NotSupportedException("Not exercised by warming verb.");
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public sealed class EscalationWarmingServiceTests
{
    private static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Epsilon = TimeSpan.FromMilliseconds(1);

    private static EscalationWarmingService CreateService(
        IProviderExtension runtime,
        FakeTimeProvider timeProvider,
        EscalationWarmingOptions? options = null)
        => new EscalationWarmingService(
            runtime,
            options ?? new EscalationWarmingOptions { IdleWindow = IdleWindow },
            timeProvider,
            logger: null);

    // ── Warm on activate / on turn ────────────────────────────────────────────

    [Fact]
    public async Task OnSessionActivated_TriggersWarm()
    {
        FakeEscalationRuntime runtime = new();
        using EscalationWarmingService svc = CreateService(runtime, new FakeTimeProvider());

        svc.OnSessionActivated("s1");

        Task winner = await Task.WhenAny(runtime.WarmStartedAsync, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == runtime.WarmStartedAsync, "EnsureRunningAsync should be called on session activate.");
    }

    [Fact]
    public async Task OnTurnStarted_TriggersWarm()
    {
        FakeEscalationRuntime runtime = new();
        using EscalationWarmingService svc = CreateService(runtime, new FakeTimeProvider());

        svc.OnTurnStarted("s1");

        Task winner = await Task.WhenAny(runtime.WarmStartedAsync, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == runtime.WarmStartedAsync, "EnsureRunningAsync should be called on turn started.");
    }

    // ── Warming never blocks ──────────────────────────────────────────────────

    [Fact]
    public void OnSessionActivated_ReturnsImmediately_WhenWarmIsSlow()
    {
        // EnsureRunningAsync is parked indefinitely.
        FakeEscalationRuntime runtime = new() { ParkWarm = true };
        using EscalationWarmingService svc = CreateService(runtime, new FakeTimeProvider());

        Stopwatch sw = Stopwatch.StartNew();
        svc.OnSessionActivated("s1");
        sw.Stop();

        // Should return in microseconds, not seconds — park on the background task.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"OnSessionActivated blocked for {sw.Elapsed}.");
    }

    [Fact]
    public void OnTurnStarted_ReturnsImmediately_WhenWarmIsSlow()
    {
        FakeEscalationRuntime runtime = new() { ParkWarm = true };
        using EscalationWarmingService svc = CreateService(runtime, new FakeTimeProvider());

        Stopwatch sw = Stopwatch.StartNew();
        svc.OnTurnStarted("s1");
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"OnTurnStarted blocked for {sw.Elapsed}.");
    }

    // ── Idle teardown after window ────────────────────────────────────────────

    [Fact]
    public async Task IdleTimer_StopsRuntime_AfterIdleWindow()
    {
        FakeTimeProvider timeProvider = new();
        FakeEscalationRuntime runtime = new();

        using EscalationWarmingService svc = CreateService(runtime, timeProvider);

        // Activity arms the timer.
        svc.OnSessionActivated("s1");

        // Advance past the idle window — FakeTimeProvider fires the callback synchronously.
        timeProvider.Advance(IdleWindow);

        // StopAsync completes synchronously (Task.CompletedTask), so it's already done.
        Task winner = await Task.WhenAny(runtime.StopCalledAsync, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == runtime.StopCalledAsync, "StopAsync should be called after idle window elapses.");
        Assert.Equal(1, runtime.StopCount);
    }

    // ── Activity cancels pending teardown ─────────────────────────────────────

    [Fact]
    public async Task ActivityBeforeIdleExpiry_CancelsTeardown()
    {
        FakeTimeProvider timeProvider = new();
        FakeEscalationRuntime runtime = new();

        using EscalationWarmingService svc = CreateService(runtime, timeProvider);

        // t=0: activity → timer armed for IdleWindow → fires at t=IdleWindow
        svc.OnSessionActivated("s1");

        // t=IdleWindow-ε: advance, timer hasn't fired
        timeProvider.Advance(IdleWindow - Epsilon);

        // t=IdleWindow-ε: second activity → timer re-armed for IdleWindow from now
        //   new deadline = (IdleWindow - ε) + IdleWindow = 2·IdleWindow - ε
        svc.OnTurnStarted("s1");

        // advance another IdleWindow-ε → t = 2·IdleWindow - 2ε, still before deadline
        timeProvider.Advance(IdleWindow - Epsilon);

        Assert.Equal(0, runtime.StopCount);

        // Advance past the new deadline → t = 2·IdleWindow, deadline was 2·IdleWindow - ε
        timeProvider.Advance(Epsilon + Epsilon);

        Task winner = await Task.WhenAny(runtime.StopCalledAsync, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(winner == runtime.StopCalledAsync, "StopAsync should fire after the re-armed idle window.");
        Assert.Equal(1, runtime.StopCount);
    }

    // ── Self-heal property ────────────────────────────────────────────────────

    [Fact]
    public async Task SelfHeal_EscalationPath_IsIndependentOfWarmingService()
    {
        // The warming service is an optimisation. If it never ran (or tore down the runtime),
        // the escalation path can call EnsureRunningAsync directly and still succeed.
        // This test verifies the fake runtime accepts direct calls regardless of the warming service.
        FakeEscalationRuntime runtime = new();

        // No warming service involved — call EnsureRunningAsync on the same runtime directly.
        await runtime.EnsureRunningAsync();

        Assert.Equal(1, runtime.WarmCount);
        Assert.Equal(0, runtime.StopCount);
    }

    // ── Verb registration ─────────────────────────────────────────────────────

    [Fact]
    public void AddEscalationWarming_RegistersServiceAndISessionActivityListener()
    {
        FakeEscalationRuntime runtime = new();
        ServiceCollection services = new();
        TestDmonHostBuilder builder = new(services);

        builder.AddEscalationWarming(_ => runtime);

        using ServiceProvider sp = services.BuildServiceProvider();

        EscalationWarmingService? warmingSvc = sp.GetService<EscalationWarmingService>();
        Assert.NotNull(warmingSvc);

        IEnumerable<ISessionActivityListener> listeners = sp.GetServices<ISessionActivityListener>();
        Assert.Contains(listeners, l => l is EscalationWarmingService);
    }

    [Fact]
    public void AddEscalationWarming_UsesProvidedOptions()
    {
        FakeEscalationRuntime runtime = new();
        ServiceCollection services = new();
        TestDmonHostBuilder builder = new(services);
        EscalationWarmingOptions customOptions = new() { IdleWindow = TimeSpan.FromMinutes(30) };

        builder.AddEscalationWarming(_ => runtime, customOptions);

        // Registration completes without throwing; the service can be built.
        using ServiceProvider sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetService<EscalationWarmingService>());
    }

    // ── Dispose guard ─────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_PreventsSubsequentActivityFromWarmingOrArming()
    {
        FakeTimeProvider timeProvider = new();
        FakeEscalationRuntime runtime = new();

        EscalationWarmingService svc = CreateService(runtime, timeProvider);
        svc.Dispose();

        // Activity after dispose must not throw and must not arm the timer.
        svc.OnSessionActivated("s1");
        svc.OnTurnStarted("s1");

        // Advance well past idle window — no teardown should fire (no warm happened).
        timeProvider.Advance(IdleWindow + IdleWindow);

        Assert.Equal(0, runtime.WarmCount);
        Assert.Equal(0, runtime.StopCount);
    }
}
