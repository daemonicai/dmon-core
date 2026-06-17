using System.Collections.Generic;
using Dmon.Protocol.Events;
using Dmon.Runtime;
using Microsoft.Reactive.Testing;

namespace Dmon.Desktop.Tests;

/// <summary>
/// Group 3.2 — scheduler-hop guarantee.
///
/// Proves that state mutations driven by <see cref="CoreSessionService.Events"/> occur ONLY
/// AFTER the injected scheduler is advanced. An event pushed by the background pump (or
/// simulated via the internal test seam) must NOT be delivered to subscribers synchronously
/// on the producing thread; the mutation must wait until the <see cref="TestScheduler"/> is
/// explicitly advanced.
///
/// Also covers 3.3 (boot state transitions) and 3.4 (idempotent teardown).
/// </summary>
public sealed class CoreSessionServiceTests
{
    // =========================================================================
    // 3.2 — scheduler-hop: mutation happens only after the scheduler is pumped
    // =========================================================================

    /// <summary>
    /// Proves the ObserveOn(_scheduler) constraint: an event pushed into the internal
    /// subject on thread A does NOT reach subscribers until the TestScheduler is advanced.
    ///
    /// The <see cref="CoreSessionService.SimulateEventForTest"/> seam lets us push an event
    /// directly into the subject without requiring a live RPC connection — isolating the
    /// scheduler-hop behaviour from process-launch concerns.
    /// </summary>
    [Fact]
    public void Events_StateMutation_OccursOnlyAfterSchedulerAdvanced()
    {
        // Arrange — a NeverLauncher (StartAsync never called in this test) and a TestScheduler.
        TestScheduler scheduler = new();
        NeverLauncher launcher = new();
        CoreSessionService sut = new(launcher, scheduler);

        List<Event> received = [];
        sut.Events.Subscribe(e => received.Add(e));

        // Act part 1 — push an event into the subject on THIS thread (simulates the pump thread).
        AgentStartEvent evt = new();
        sut.SimulateEventForTest(evt);

        // Assert part 1 — the event has been queued on the scheduler but NOT yet delivered.
        Assert.Empty(received);

        // Act part 2 — advance the scheduler; it drains its queued notification work.
        scheduler.AdvanceBy(1);

        // Assert part 2 — the event is now delivered, exactly once.
        Assert.Single(received);
        Assert.Equal(evt, received[0]);
    }

    /// <summary>
    /// Proves the same scheduler-hop guarantee for multiple events:
    /// pushing N events without advancing the scheduler delivers nothing;
    /// advancing past all queued items delivers all N events.
    /// Each OnNext is a separate scheduler work item; AdvanceBy(N ticks) drains N items.
    /// </summary>
    [Fact]
    public void Events_MultipleEventsQueued_AllDeliveredAfterSchedulerDrained()
    {
        TestScheduler scheduler = new();
        NeverLauncher launcher = new();
        CoreSessionService sut = new(launcher, scheduler);

        List<Event> received = [];
        sut.Events.Subscribe(e => received.Add(e));

        // Push three events without advancing.
        sut.SimulateEventForTest(new AgentStartEvent());
        sut.SimulateEventForTest(new AgentStartEvent());
        sut.SimulateEventForTest(new AgentStartEvent());

        Assert.Empty(received); // not delivered yet

        // Each event is a separate scheduler work item; advance past all of them.
        scheduler.AdvanceBy(3);

        Assert.Equal(3, received.Count);
    }

    // =========================================================================
    // 3.3 — boot state: Booting → Ready transition delivered via scheduler
    // =========================================================================

    [Fact]
    public async Task StartAsync_OnSuccess_TransitionsCurrentStateToReady()
    {
        // Arrange
        TestScheduler scheduler = new();
        InstantLauncher launcher = new();

        await using CoreSessionService sut = new(launcher, scheduler);

        // Act
        await sut.StartAsync(CancellationToken.None);

        // CurrentState is updated synchronously in StartAsync (before the scheduler hop)
        // so it is always observable regardless of the scheduler.
        Assert.Equal(CoreState.Ready, sut.CurrentState);
    }

    [Fact]
    public async Task StartAsync_OnSuccess_StateObservableDeliversReadyAfterSchedulerAdvance()
    {
        // Arrange
        TestScheduler scheduler = new();
        InstantLauncher launcher = new();

        await using CoreSessionService sut = new(launcher, scheduler);

        List<CoreState> states = [];
        sut.State.Subscribe(s => states.Add(s));

        // The BehaviorSubject emits Booting immediately on subscribe — but ObserveOn queues it.
        scheduler.AdvanceBy(1); // drain initial Booting

        // Act
        await sut.StartAsync(CancellationToken.None);

        // The Ready notification is queued — not yet delivered.
        Assert.DoesNotContain(CoreState.Ready, states);

        scheduler.AdvanceBy(1); // drain Ready

        // Assert
        Assert.Contains(CoreState.Ready, states);
        Assert.Equal([CoreState.Booting, CoreState.Ready], states);
    }

    // =========================================================================
    // 3.3 — boot state: Faulted on CoreAcquisitionException
    // =========================================================================

    [Fact]
    public async Task StartAsync_OnCoreAcquisitionFault_CurrentStateIsFaulted()
    {
        TestScheduler scheduler = new();
        FaultingLauncher launcher = new(new CoreAcquisitionException("no core found"));

        await using CoreSessionService sut = new(launcher, scheduler);

        await sut.StartAsync(CancellationToken.None);

        Assert.Equal(CoreState.Faulted, sut.CurrentState);
        Assert.NotNull(sut.FaultMessage);
        Assert.Contains("no core found", sut.FaultMessage);
    }

    [Fact]
    public async Task StartAsync_OnProtocolMismatchFault_CurrentStateIsFaulted()
    {
        TestScheduler scheduler = new();
        FaultingLauncher launcher = new(new ProtocolMismatchException("0.1", "0.2"));

        await using CoreSessionService sut = new(launcher, scheduler);

        await sut.StartAsync(CancellationToken.None);

        Assert.Equal(CoreState.Faulted, sut.CurrentState);
        Assert.NotNull(sut.FaultMessage);
    }

    // =========================================================================
    // 3.4 — teardown: DisposeAsync is idempotent
    // =========================================================================

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        TestScheduler scheduler = new();
        NeverLauncher launcher = new();

        CoreSessionService sut = new(launcher, scheduler);

        // First dispose.
        await sut.DisposeAsync();

        // Second dispose must not throw.
        Exception? caught = await Record.ExceptionAsync(async () => await sut.DisposeAsync());
        Assert.Null(caught);
    }

    [Fact]
    public async Task DisposeAsync_AfterSuccessfulStart_CompletesEventStream()
    {
        TestScheduler scheduler = new();
        InstantLauncher launcher = new();

        await using CoreSessionService sut = new(launcher, scheduler);
        await sut.StartAsync(CancellationToken.None);

        bool completed = false;
        sut.Events.Subscribe(
            onNext: _ => { },
            onCompleted: () => completed = true);

        await sut.DisposeAsync();
        scheduler.AdvanceBy(1);

        Assert.True(completed);
    }

    // =========================================================================
    // Test doubles
    // =========================================================================

    /// <summary>
    /// Launcher that never completes — used when <see cref="StartAsync"/> is not called.
    /// </summary>
    private sealed class NeverLauncher : ICoreLauncher
    {
        public Task<CoreSession> StartProtocolCompatibleCoreAsync(
            string? corePathOverride = null,
            string? workingDirectory = null,
            Action<string>? onStderrLine = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CoreSession>(new InvalidOperationException("NeverLauncher does not start."));
    }

    /// <summary>
    /// Launcher that resolves immediately with a minimal fake session.
    /// The fake process's StandardOutput returns nothing; the RpcClient pump will block
    /// (on Task.Delay.Infinite via the CancellationToken) until teardown cancels it.
    /// </summary>
    private sealed class InstantLauncher : ICoreLauncher
    {
        public Task<CoreSession> StartProtocolCompatibleCoreAsync(
            string? corePathOverride = null,
            string? workingDirectory = null,
            Action<string>? onStderrLine = null,
            CancellationToken cancellationToken = default)
        {
            FakeCoreProcess process = new(cancellationToken);
            AgentReadyEvent ready = new() { ProtocolVersion = "0.0", CoreVersion = "0.0" };
            return Task.FromResult(new CoreSession(process, ready));
        }
    }

    /// <summary>
    /// Launcher that throws a given exception synchronously on start.
    /// </summary>
    private sealed class FaultingLauncher : ICoreLauncher
    {
        private readonly Exception _fault;

        public FaultingLauncher(Exception fault)
        {
            _fault = fault;
        }

        public Task<CoreSession> StartProtocolCompatibleCoreAsync(
            string? corePathOverride = null,
            string? workingDirectory = null,
            Action<string>? onStderrLine = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CoreSession>(_fault);
    }

    /// <summary>
    /// Minimal <see cref="ICoreProcess"/> whose StandardOutput blocks until cancelled.
    /// This keeps the RpcClient transport pump alive without producing any events,
    /// which is correct for tests that drive events via <c>SimulateEventForTest</c>.
    /// </summary>
    private sealed class FakeCoreProcess : ICoreProcess
    {
        private readonly CancellationToken _lifetime;
        private readonly StringWriter _stdin = new();

        public FakeCoreProcess(CancellationToken lifetime)
        {
            _lifetime = lifetime;
            StandardOutput = new BlockingTextReader(lifetime);
        }

        public TextReader StandardOutput { get; }
        public TextWriter StandardInput => _stdin;
        public bool IsRunning => true;

        public Task StartAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task RestartAsync() => Task.CompletedTask;

        public void Dispose() { }
    }

    /// <summary>
    /// TextReader that never returns a line — blocks until cancellation.
    /// Simulates an idle stdout pipe so the transport pump stays alive but produces nothing.
    /// </summary>
    private sealed class BlockingTextReader : TextReader
    {
        private readonly CancellationToken _cancellationToken;

        public BlockingTextReader(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
            return null; // unreachable; delay throws OperationCanceledException
        }
    }
}
