using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Dmon.Gateway.Sessions;

/// <summary>
/// Background service that periodically scans the session registry and reaps handlers that have
/// been detached long enough. Runs on a configurable interval driven by <see cref="TimeProvider"/>
/// so tests can use <c>FakeTimeProvider</c> to advance time deterministically.
///
/// Reaping policy (design D4 / Group 7):
/// <list type="bullet">
///   <item>Idle detached (no turn in flight): reap after <see cref="GatewayOptions.IdleDetachedTtlMinutes"/>.</item>
///   <item>Detached with turn in flight: reap after <see cref="GatewayOptions.RunningTurnTtlMinutes"/>
///         (absolute maximum — reaps even if the turn never completes).</item>
///   <item>Attached handlers are never reaped by this service.</item>
/// </list>
///
/// Reaping = remove from registry + <see cref="SessionHandler.StopAsync"/> +
/// <see cref="SessionHandler.DisposeAsync"/> (terminates the dmoncore process).
/// </summary>
public sealed class SessionReaper : BackgroundService
{
    private readonly SessionRegistry _registry;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionReaper> _logger;

    // Scan interval. Fine enough to catch TTLs without busy-looping.
    // A separate configurable scan interval is not required by the spec; 30s is a reasonable
    // floor for TTLs measured in minutes.
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);

    public SessionReaper(
        SessionRegistry registry,
        IOptionsMonitor<GatewayOptions> options,
        TimeProvider timeProvider,
        ILogger<SessionReaper> logger)
    {
        _registry = registry;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ScanInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await ScanAndReapAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Exposed for testing: runs one reap scan synchronously against the current registry
    /// and <c>TimeProvider.GetUtcNow()</c>. Tests advance <c>FakeTimeProvider</c> and then
    /// call this directly rather than waiting for the background loop.
    /// </summary>
    internal async Task ScanAndReapAsync(CancellationToken cancellationToken)
    {
        GatewayOptions opts = _options.CurrentValue;
        TimeSpan idleTtl = TimeSpan.FromMinutes(opts.IdleDetachedTtlMinutes);
        TimeSpan absoluteMax = TimeSpan.FromMinutes(opts.RunningTurnTtlMinutes);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        IReadOnlyList<(string SessionId, SessionHandler Handler)> snapshot = _registry.Snapshot();

        foreach ((string sessionId, SessionHandler handler) in snapshot)
        {
            DateTimeOffset? detachedAt = handler.DetachedAt;
            if (detachedAt is null)
                continue; // Connected handler — never reaped here.

            TimeSpan detachedFor = now - detachedAt.Value;
            bool turnInFlight = handler.IsTurnInFlight;

            bool shouldReap = turnInFlight
                ? detachedFor >= absoluteMax          // in-flight: hard ceiling
                : detachedFor >= idleTtl;             // idle: normal TTL

            if (!shouldReap)
                continue;

            // Remove from registry before stopping so no new attaches can see it.
            SessionHandler? removed = _registry.Remove(sessionId);
            if (removed is null)
                continue; // Already removed by a concurrent reap or detach.

            _logger.LogInformation(
                "Reaping session {SessionId}: detachedFor={DetachedFor}, turnInFlight={TurnInFlight}.",
                sessionId, detachedFor, turnInFlight);

            try
            {
                await removed.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error stopping reaped session {SessionId}.", sessionId);
            }

            try
            {
                await removed.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error disposing reaped session {SessionId}.", sessionId);
            }
        }
    }
}
