using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Dmon.Protocol.Events;
using Dmon.Runtime;

namespace Dmon.Desktop;

/// <summary>
/// Manages the lifecycle of the local dmoncore process for the desktop host.
///
/// Responsibilities:
/// - Launches the core via <see cref="ICoreLauncher.StartProtocolCompatibleCoreAsync"/> (3.1).
/// - A background pump (<see cref="PumpEventsAsync"/>) drains <see cref="IRpcClient.Events"/>
///   into a <see cref="Subject{T}"/>; <see cref="Events"/> and <see cref="State"/> are
///   exposed as <c>subject.ObserveOn(_scheduler)</c> so subscribers always receive on the
///   injected scheduler, never on the pump thread (3.2).
/// - Exposes <see cref="State"/> (<see cref="IObservable{T}"/> of <see cref="CoreState"/>)
///   so views gate interaction on core readiness (3.3).
/// - Disposes the RPC client and stops the core on teardown; idempotent (3.4).
///
/// Production usage: inject <see cref="RxSchedulers.MainThreadScheduler"/> as the scheduler.
/// Test usage: inject a <see cref="Microsoft.Reactive.Testing.TestScheduler"/> to assert
/// that state mutations do not happen before the scheduler is advanced.
/// </summary>
public sealed class CoreSessionService : IAsyncDisposable
{
    private readonly ICoreLauncher _launcher;
    private readonly IScheduler _scheduler;
    private readonly string? _corePathOverride;

    // BehaviorSubject so late subscribers see the current state immediately.
    private readonly BehaviorSubject<CoreState> _stateSubject = new(CoreState.Booting);

    // Subject for the event observable — completed on teardown.
    private readonly Subject<Event> _eventSubject = new();

    private CoreState _currentState = CoreState.Booting;
    private string? _faultMessage;
    private IRpcClient? _client;
    private CoreSession? _session;

    // Guards teardown from concurrent/double invocation.
    private int _disposed;

    // Ensures each subject is completed at most once (EOF path, fault paths, DisposeAsync).
    private int _subjectsCompleted;

    // Per-session pump cancellation; cancelled before reload or dispose.
    private CancellationTokenSource _sessionCts = new();

    /// <summary>
    /// Creates a <see cref="CoreSessionService"/>.
    /// </summary>
    /// <param name="launcher">
    /// Core launcher. Inject via DI; use <see cref="CoreLauncher"/> in production.
    /// Only <see cref="CoreLauncher"/> supports <see cref="ReloadAsync"/>; a fake launcher
    /// will throw <see cref="NotSupportedException"/> if <see cref="ReloadAsync"/> is called.
    /// </param>
    /// <param name="scheduler">
    /// Scheduler to marshal events onto before they reach observers.
    /// Pass <see cref="ReactiveUI.RxSchedulers.MainThreadScheduler"/> in production;
    /// pass a <see cref="Microsoft.Reactive.Testing.TestScheduler"/> in tests.
    /// </param>
    /// <param name="corePathOverride">
    /// Value of the <c>--core-path</c> CLI argument, or <see langword="null"/>.
    /// </param>
    public CoreSessionService(
        ICoreLauncher launcher,
        IScheduler scheduler,
        string? corePathOverride = null)
    {
        _launcher = launcher;
        _scheduler = scheduler;
        _corePathOverride = corePathOverride;

        // _eventSubject and _stateSubject are fed by PumpEventsAsync on a background thread;
        // ObserveOn ensures subscribers always receive on the injected scheduler.
        Events = _eventSubject.ObserveOn(_scheduler);
        State = _stateSubject.ObserveOn(_scheduler);
    }

    /// <summary>
    /// Hot stream of <see cref="CoreState"/> transitions, marshalled onto the injected scheduler.
    /// The first value emitted is always <see cref="CoreState.Booting"/>
    /// (sourced from the underlying <see cref="BehaviorSubject{T}"/>).
    /// </summary>
    public IObservable<CoreState> State { get; }

    /// <summary>
    /// Snapshot of the current boot state. Safe to read from any thread; reflects the last
    /// value written by the pump/launcher task (not gated by the scheduler).
    /// </summary>
    public CoreState CurrentState => _currentState;

    /// <summary>
    /// Fault message when <see cref="CurrentState"/> is <see cref="CoreState.Faulted"/>,
    /// otherwise <see langword="null"/>.
    /// </summary>
    public string? FaultMessage => _faultMessage;

    /// <summary>
    /// The live RPC client. Non-null only when <see cref="CurrentState"/> is <see cref="CoreState.Ready"/>.
    /// </summary>
    public IRpcClient? Client => _client;

    /// <summary>
    /// All events from the core, marshalled onto the injected <see cref="IScheduler"/>.
    /// The stream completes when the pump exits (core EOF or teardown).
    /// </summary>
    public IObservable<Event> Events { get; }

    /// <summary>
    /// Launches the core, builds the RPC client, and starts the event pump.
    /// <see cref="State"/> transitions to <see cref="CoreState.Ready"/> (or
    /// <see cref="CoreState.Faulted"/>) on the injected scheduler once launch completes.
    /// Subscribe to <see cref="State"/> and <see cref="Events"/> BEFORE calling this method.
    /// </summary>
    /// <param name="cancellationToken">Outer lifetime token (process shutdown).</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // Link the session CTS to the outer token so process shutdown unblocks the pump.
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            CoreSession session = await _launcher
                .StartProtocolCompatibleCoreAsync(
                    _corePathOverride,
                    cancellationToken: _sessionCts.Token)
                .ConfigureAwait(false);

            _session = session;

            // Build the RPC client and start pumping BEFORE calling StartAsync so no events
            // are missed (mirrors the Terminal discipline exactly).
            IRpcClient client = new RpcClient(new CoreProcessRpcTransport(session.Process));
            _client = client;

            // Start the background pump task (fire-and-forget; it lives until teardown).
            _ = PumpEventsAsync(client, _sessionCts.Token);

            await client.StartAsync(_sessionCts.Token).ConfigureAwait(false);

            _currentState = CoreState.Ready;
            _stateSubject.OnNext(CoreState.Ready);
        }
        catch (CoreAcquisitionException ex)
        {
            _currentState = CoreState.Faulted;
            _faultMessage = ex.Message;
            _stateSubject.OnNext(CoreState.Faulted);
            CompleteSubjects();
        }
        catch (ProtocolMismatchException ex)
        {
            _currentState = CoreState.Faulted;
            _faultMessage = ex.Message;
            _stateSubject.OnNext(CoreState.Faulted);
            CompleteSubjects();
        }
        catch (OperationCanceledException)
        {
            CompleteSubjects();
            throw;
        }
    }

    /// <summary>
    /// Reloads the core: disposes the current RPC client, restarts the core process via
    /// <see cref="CoreLauncher.RestartAsync"/>, and rebuilds the client.
    /// Only available when the injected launcher is a <see cref="CoreLauncher"/> instance.
    /// This is the service-level seam for Group 6.4's reload UI.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException">
    /// Thrown when the injected launcher does not support restart (e.g. a test fake).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the core is not currently running.
    /// </exception>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_launcher is not CoreLauncher coreLauncher)
            throw new NotSupportedException("ReloadAsync requires the production CoreLauncher.");

        if (_session is null || _client is null)
            throw new InvalidOperationException("Cannot reload: core is not running.");

        // Cancel the session pump before disposing the client (Terminal discipline).
        await _sessionCts.CancelAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);

        // RestartAsync reuses the same process manager — do NOT dispose the old session.
        _session = await coreLauncher
            .RestartAsync(_session, cancellationToken)
            .ConfigureAwait(false);

        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        IRpcClient client = new RpcClient(new CoreProcessRpcTransport(_session.Process));
        _client = client;

        _ = PumpEventsAsync(client, _sessionCts.Token);

        await client.StartAsync(_sessionCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Tears down the session: cancels the pump, disposes the RPC client, then stops the core.
    /// Idempotent — safe to call multiple times or concurrently.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _sessionCts.CancelAsync().ConfigureAwait(false);

        if (_client is not null)
            await _client.DisposeAsync().ConfigureAwait(false);

        if (_session is not null)
            await _session.Process.StopAsync().ConfigureAwait(false);

        _sessionCts.Dispose();
        CompleteSubjects();
    }

    /// <summary>
    /// Feeds an event directly into the internal subject — bypasses the RPC pump.
    /// Intended only for unit tests that need to prove the scheduler-hop guarantee
    /// without wiring up a real <see cref="IRpcClient"/> and transport.
    /// </summary>
    internal void SimulateEventForTest(Event evt) => _eventSubject.OnNext(evt);

    // Completes both subjects at most once across all exit paths (EOF, fault, cancel, dispose).
    private void CompleteSubjects()
    {
        if (Interlocked.Exchange(ref _subjectsCompleted, 1) != 0)
            return;
        _stateSubject.OnCompleted();
        _eventSubject.OnCompleted();
    }

    // Background pump: drains client.Events and forwards each event to _eventSubject.
    // Because _eventSubject.ObserveOn(_scheduler) is applied in the constructor, all
    // subscribers receive events on the injected scheduler — never on this background thread.
    private async Task PumpEventsAsync(IRpcClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (Event evt in client.Events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                _eventSubject.OnNext(evt);
            }
            CompleteSubjects();
        }
        catch (OperationCanceledException)
        {
            // Normal teardown — do not propagate an error to subscribers.
        }
        catch (Exception ex)
        {
            _eventSubject.OnError(ex);
        }
    }
}
