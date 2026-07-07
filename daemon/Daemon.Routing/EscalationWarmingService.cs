using Dmon.Abstractions.Hosting;
using Dmon.Abstractions.Providers;
using Microsoft.Extensions.Logging;

namespace Daemon.Routing;

/// <summary>
/// Warms the escalation runtime on session activity and tears it down after an idle window.
/// Fire-and-forget: warming never blocks a session command or turn (ADR-034 D2; spec).
/// </summary>
/// <remarks>
/// Correctness does NOT depend on warming having completed. An escalation that arrives before
/// warming finishes — or just after idle teardown — still succeeds: the escalation backend's
/// request path wraps every dispatch in a respawn that calls
/// <see cref="IProviderExtension.EnsureRunningAsync"/> before it reaches the inner client
/// (<c>Dmon.Providers.Mlx.EnsureRunningChatClient</c>), attaching to (or respawning) the runtime
/// as needed. This is a real request-path self-heal, not merely a warming optimisation.
/// </remarks>
internal sealed class EscalationWarmingService : ISessionActivityListener, IDisposable
{
    private readonly IProviderExtension _escalationRuntime;
    private readonly EscalationWarmingOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EscalationWarmingService>? _logger;
    private readonly ITimer _idleTimer;
    private volatile bool _disposed;

    // UTC ticks of the most recent activity, updated via Interlocked so concurrent
    // session notifications are thread-safe without a lock.
    private long _lastActivityTicks;

    internal EscalationWarmingService(
        IProviderExtension escalationRuntime,
        EscalationWarmingOptions options,
        TimeProvider timeProvider,
        ILogger<EscalationWarmingService>? logger)
    {
        _escalationRuntime = escalationRuntime;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _lastActivityTicks = timeProvider.GetUtcNow().UtcDateTime.Ticks;
        // Start unarmed; first activity arms the timer.
        _idleTimer = timeProvider.CreateTimer(OnIdleTimerFired, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void OnSessionActivated(string sessionId) => RecordActivity();

    /// <inheritdoc />
    public void OnTurnStarted(string sessionId) => RecordActivity();

    private void RecordActivity()
    {
        if (_disposed) return;

        // Record time first so the callback's recheck sees the updated deadline.
        Interlocked.Exchange(ref _lastActivityTicks, _timeProvider.GetUtcNow().UtcDateTime.Ticks);

        // ITimer.Change is thread-safe: concurrent resets push the deadline out.
        _idleTimer.Change(_options.IdleWindow, Timeout.InfiniteTimeSpan);

        // Detach; exceptions are swallowed inside WarmAsync.
        _ = WarmAsync();
    }

    private async Task WarmAsync()
    {
        try
        {
            await _escalationRuntime.EnsureRunningAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Escalation warming failed; escalation path will spawn on demand.");
        }
    }

    private void OnIdleTimerFired(object? state)
    {
        if (_disposed) return;

        DateTimeOffset now = _timeProvider.GetUtcNow();
        long lastActivityTicks = Interlocked.Read(ref _lastActivityTicks);
        DateTimeOffset deadline = new DateTimeOffset(lastActivityTicks, TimeSpan.Zero) + _options.IdleWindow;

        if (now < deadline)
        {
            // Activity arrived since this callback was armed; re-arm for the remaining window.
            TimeSpan remaining = deadline - now;
            _idleTimer.Change(remaining, Timeout.InfiniteTimeSpan);
            return;
        }

        _ = TeardownAsync();
    }

    private async Task TeardownAsync()
    {
        try
        {
            await _escalationRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Escalation idle teardown failed.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _idleTimer.Dispose();
    }
}
