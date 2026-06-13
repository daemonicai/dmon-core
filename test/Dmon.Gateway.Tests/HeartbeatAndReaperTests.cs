using System.Threading.Channels;
using Dmon.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 7: heartbeat liveness, detached grace timer, idle-reaping, in-flight retention,
/// absolute-max TTL enforcement, and concurrent-handler cap.
/// </summary>
public sealed class HeartbeatAndReaperTests
{
    // -------------------------------------------------------------------------
    // 7.1 — Heartbeat: ping emitted on interval; missed pong → detach
    // -------------------------------------------------------------------------

    /// <summary>
    /// 7.1a — A ping frame is sent to the client after one heartbeat interval elapses.
    /// </summary>
    [Fact]
    public async Task Heartbeat_SendsPing_AfterInterval()
    {
        FakeTimeProvider time = new();
        GatewayOptions opts = new() { HeartbeatIntervalSeconds = 10 };
        StaticOptionsMonitor options = new(opts);

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin, time);

        RecordingConnection connection = new();
        handler.Attach(connection, lastSeq: 0);

        GatewayConnectionEndpoint endpoint = new(
            new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions { Options = options, TimeProvider = time },
            NullLogger<GatewayConnectionEndpoint>.Instance);

        FakeClientWebSocket socket = new();

        using CancellationTokenSource cts = new();
        Task loopTask = endpoint.RunForwardingLoopForTestAsync(
            socket, connection, handler, cts.Token,
            enableHeartbeat: true);

        // Yield so the heartbeat background task can start and register its first timer.
        await Task.Delay(50);

        // Advance past one interval — the heartbeat task should send a ping.
        time.Advance(TimeSpan.FromSeconds(11));

        // Give the loop a moment to process the timer tick.
        await Task.Delay(200);

        // Cancel the loop so the test terminates cleanly.
        await cts.CancelAsync();
        socket.QueueClose();
        try { await loopTask; } catch (OperationCanceledException) { }

        // A ping frame must have been sent via the serialized connection funnel.
        bool pingEmitted = connection.Frames.Any(f => f.Contains("\"ping\""));
        Assert.True(pingEmitted, $"Expected a ping frame. Frames: [{string.Join(", ", connection.Frames)}]");
    }

    /// <summary>
    /// 7.1b — When no pong (or any frame) arrives within the missed-beat deadline (2× interval),
    /// the forwarding loop exits. The missed-beat path is exercised by advancing a
    /// <see cref="FakeTimeProvider"/> twice: once past the ping interval (triggers ping send)
    /// and once past the deadline window (triggers missed-beat → <c>heartbeatCts</c> cancel →
    /// forwarding loop exits). No real wall-clock delays are used.
    ///
    /// Assertion: loop task completes AND a ping was sent AND no explicit CT was cancelled by
    /// the test — the missed-beat code path is the only explanation for loop exit.
    /// </summary>
    [Fact]
    public async Task Heartbeat_MissedPong_ExitsForwardingLoop()
    {
        FakeTimeProvider time = new();
        GatewayOptions opts = new() { HeartbeatIntervalSeconds = 10 };
        StaticOptionsMonitor options = new(opts);

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin, time);

        RecordingConnection connection = new();
        handler.Attach(connection, lastSeq: 0);

        GatewayConnectionEndpoint endpoint = new(
            new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions { Options = options, TimeProvider = time },
            NullLogger<GatewayConnectionEndpoint>.Instance);

        FakeClientWebSocket socket = new();
        // No pong queued — socket stays open but silent.

        // No cancellation token from the test; the only expected exit path is heartbeatCts.
        Task loopTask = endpoint.RunForwardingLoopForTestAsync(
            socket, connection, handler, CancellationToken.None,
            enableHeartbeat: true);

        // Yield so the heartbeat background task can start and register its first Task.Delay.
        await Task.Delay(50);

        // Advance past the first interval: the heartbeat fires and sends a ping.
        time.Advance(TimeSpan.FromSeconds(11));

        // Yield so the heartbeat task processes the timer tick and reaches the second Delay.
        await Task.Delay(50);

        // Advance past the deadline window (2× interval): missed-beat detected → heartbeatCts
        // cancelled → ReceiveAsync unblocks → forwarding loop exits.
        time.Advance(TimeSpan.FromSeconds(21));

        // Wait for the loop task to complete; the FakeTimeProvider advance should make this
        // near-instant but allow a short real-clock budget for task scheduling.
        await WaitForTaskAsync(loopTask, TimeSpan.FromSeconds(3));

        Assert.True(loopTask.IsCompleted,
            "Forwarding loop must exit after missed-beat deadline (no pong, clock advanced past 2× interval).");

        // A ping must have been sent via the serialized connection funnel.
        bool pingEmitted = connection.Frames.Any(f => f.Contains("\"ping\""));
        Assert.True(pingEmitted,
            $"A ping must have been sent before the missed-beat was declared. Frames: [{string.Join(", ", connection.Frames)}]");
    }

    // -------------------------------------------------------------------------
    // 7.2 — Detached grace timer and idle TTL reaping
    // -------------------------------------------------------------------------

    /// <summary>
    /// 7.2a — A detached handler with no turn in flight is reaped after the idle TTL.
    /// </summary>
    [Fact]
    public async Task Reaper_IdleDetachedHandler_ReapedAfterIdleTtl()
    {
        FakeTimeProvider time = new();
        SessionRegistry registry = new();
        IOptionsMonitor<GatewayOptions> options = new StaticOptionsMonitor(new GatewayOptions
        {
            IdleDetachedTtlMinutes = 15,
            RunningTurnTtlMinutes = 60,
        });

        FeedableReader stdout = new();
        StringWriter stdin = new();
        SessionHandler handler = new("s1", stdout, stdin, time);
        registry.Register("s1", handler);

        // Detach so the grace timer starts.
        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);
        handler.Detach(conn);
        Assert.NotNull(handler.DetachedAt);
        Assert.False(handler.IsTurnInFlight);

        SessionReaper reaper = new(registry, options, time,
            NullLogger<SessionReaper>.Instance);

        // Advance to just before the TTL — must NOT reap.
        time.Advance(TimeSpan.FromMinutes(14));
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.NotNull(registry.TryGet("s1")); // still registered

        // Advance past the TTL — must reap.
        time.Advance(TimeSpan.FromMinutes(2)); // total = 16 min > 15 min TTL
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.Null(registry.TryGet("s1")); // removed from registry
    }

    /// <summary>
    /// 7.2b — An attached handler is never reaped regardless of elapsed time.
    /// </summary>
    [Fact]
    public async Task Reaper_AttachedHandler_NeverReaped()
    {
        FakeTimeProvider time = new();
        SessionRegistry registry = new();
        IOptionsMonitor<GatewayOptions> options = new StaticOptionsMonitor(new GatewayOptions
        {
            IdleDetachedTtlMinutes = 1,
            RunningTurnTtlMinutes = 1,
        });

        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin, time);
        registry.Register("s1", handler);

        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);
        // DetachedAt is null — handler is currently attached.
        Assert.Null(handler.DetachedAt);

        SessionReaper reaper = new(registry, options, time,
            NullLogger<SessionReaper>.Instance);

        time.Advance(TimeSpan.FromHours(1));
        await reaper.ScanAndReapAsync(CancellationToken.None);

        Assert.NotNull(registry.TryGet("s1")); // must survive
    }

    // -------------------------------------------------------------------------
    // 7.3 — In-flight turn retention and absolute-max TTL
    // -------------------------------------------------------------------------

    /// <summary>
    /// 7.3a — A detached handler with a turn in flight is NOT reaped at the idle TTL
    /// but IS reaped at the absolute maximum.
    /// </summary>
    [Fact]
    public async Task Reaper_InFlightDetachedHandler_NotReapedAtIdleTtl_ReapedAtAbsoluteMax()
    {
        FakeTimeProvider time = new();
        SessionRegistry registry = new();
        IOptionsMonitor<GatewayOptions> options = new StaticOptionsMonitor(new GatewayOptions
        {
            IdleDetachedTtlMinutes = 15,
            RunningTurnTtlMinutes = 60,
        });

        FeedableReader stdout = new();
        StringWriter stdin = new();
        SessionHandler handler = new("s1", stdout, stdin, time);
        registry.Register("s1", handler);

        // Attach, then feed a turnStart to put a turn in flight.
        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);
        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");

        // Wait for the reader to process the line and set IsTurnInFlight.
        await WaitForConditionAsync(() => handler.IsTurnInFlight, TimeSpan.FromSeconds(5));
        Assert.True(handler.IsTurnInFlight);

        // Detach — the grace timer starts.
        handler.Detach(conn);
        Assert.NotNull(handler.DetachedAt);

        SessionReaper reaper = new(registry, options, time,
            NullLogger<SessionReaper>.Instance);

        // Advance past idle TTL — must NOT reap (turn in flight).
        time.Advance(TimeSpan.FromMinutes(16));
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.NotNull(registry.TryGet("s1")); // still alive

        // Advance past absolute max — must reap.
        time.Advance(TimeSpan.FromMinutes(50)); // total > 60 min absolute max
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.Null(registry.TryGet("s1")); // reaped
    }

    /// <summary>
    /// 7.3b — After turnEnd, the handler reverts to idle (IsTurnInFlight = false) and
    /// is subject to the idle TTL on the next reap scan.
    /// </summary>
    [Fact]
    public async Task Reaper_TurnCompletes_ThenIdleTtlApplies()
    {
        FakeTimeProvider time = new();
        SessionRegistry registry = new();
        IOptionsMonitor<GatewayOptions> options = new StaticOptionsMonitor(new GatewayOptions
        {
            IdleDetachedTtlMinutes = 15,
            RunningTurnTtlMinutes = 60,
        });

        FeedableReader stdout = new();
        StringWriter stdin = new();
        SessionHandler handler = new("s1", stdout, stdin, time);
        registry.Register("s1", handler);

        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);

        // Start a turn then end it using the real protocol events the core emits.
        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        await WaitForConditionAsync(() => handler.IsTurnInFlight, TimeSpan.FromSeconds(5));
        stdout.Feed("""{"type":"turnEnd","sessionId":"s1"}""");
        await WaitForConditionAsync(() => !handler.IsTurnInFlight, TimeSpan.FromSeconds(5));
        Assert.False(handler.IsTurnInFlight);

        // Detach.
        handler.Detach(conn);

        SessionReaper reaper = new(registry, options, time,
            NullLogger<SessionReaper>.Instance);

        // Advance past idle TTL — should now reap (no turn in flight).
        time.Advance(TimeSpan.FromMinutes(16));
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.Null(registry.TryGet("s1")); // reaped after turn completed
    }

    // -------------------------------------------------------------------------
    // 7.3c — Concurrent-handler cap
    // -------------------------------------------------------------------------

    /// <summary>
    /// 7.3c — TryRegister rejects registrations beyond the cap while allowing registrations
    /// below the cap.
    /// </summary>
    [Fact]
    public async Task SessionRegistry_TryRegister_EnforcesCap()
    {
        SessionRegistry registry = new();

        FeedableReader stdout1 = new(), stdout2 = new(), stdout3 = new();
        StringWriter stdin1 = new(), stdin2 = new(), stdin3 = new();
        await using SessionHandler h1 = new("s1", stdout1, stdin1);
        await using SessionHandler h2 = new("s2", stdout2, stdin2);
        await using SessionHandler h3 = new("s3", stdout3, stdin3);

        const int cap = 2;

        Assert.True(registry.TryRegister("s1", h1, cap), "First registration must succeed");
        Assert.True(registry.TryRegister("s2", h2, cap), "Second registration must succeed");
        Assert.False(registry.TryRegister("s3", h3, cap), "Third registration must be rejected (cap=2)");

        Assert.Equal(2, registry.Count);
        Assert.Null(registry.TryGet("s3"));
        Assert.NotNull(registry.TryGet("s1"));
        Assert.NotNull(registry.TryGet("s2"));
    }

    /// <summary>
    /// 7.3d — Re-registering an existing session id replaces the handler without consuming
    /// extra capacity (the count stays the same).
    /// </summary>
    [Fact]
    public async Task SessionRegistry_TryRegister_ReplacesExistingSessionWithoutCountingAgainstCap()
    {
        SessionRegistry registry = new();

        FeedableReader stdout1 = new(), stdout2 = new();
        StringWriter stdin1 = new(), stdin2 = new();
        await using SessionHandler h1a = new("s1", stdout1, stdin1);
        await using SessionHandler h1b = new("s1", stdout2, stdin2);

        const int cap = 1;

        Assert.True(registry.TryRegister("s1", h1a, cap));
        Assert.Equal(1, registry.Count);

        // Re-registering s1 must succeed even though count == cap.
        Assert.True(registry.TryRegister("s1", h1b, cap));
        Assert.Equal(1, registry.Count);
        Assert.Same(h1b, registry.TryGet("s1"));
    }

    // -------------------------------------------------------------------------
    // In-flight flag — unit tests on SessionHandler directly
    // -------------------------------------------------------------------------

    [Fact]
    public async Task IsTurnInFlight_SetOnTurnStart_ClearedOnTurnEnd()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        Assert.False(handler.IsTurnInFlight);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        await WaitForConditionAsync(() => handler.IsTurnInFlight, TimeSpan.FromSeconds(5));
        Assert.True(handler.IsTurnInFlight);

        stdout.Feed("""{"type":"turnEnd","sessionId":"s1"}""");
        await WaitForConditionAsync(() => !handler.IsTurnInFlight, TimeSpan.FromSeconds(5));
        Assert.False(handler.IsTurnInFlight);
    }

    /// <summary>
    /// An error event mid-turn must NOT clear IsTurnInFlight. ErrorEvent is emitted in many
    /// non-terminal, mid-turn contexts (e.g. turnInProgress while a turn IS running); treating
    /// it as a turn-end would let the reaper use the idle TTL while the core is still working.
    /// The turn is bracketed solely by turnStart/turnEnd.
    /// </summary>
    [Fact]
    public async Task IsTurnInFlight_ErrorEvent_DoesNotClearInFlightFlag()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        await WaitForConditionAsync(() => handler.IsTurnInFlight, TimeSpan.FromSeconds(5));

        // An error event during a running turn must leave IsTurnInFlight true.
        stdout.Feed("""{"type":"error","message":"boom"}""");
        await Task.Delay(100);
        Assert.True(handler.IsTurnInFlight,
            "An error event must not clear IsTurnInFlight; only turnEnd does.");
    }

    [Fact]
    public async Task IsTurnInFlight_SecondTurnStart_IsIdempotent()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        await WaitForConditionAsync(() => handler.IsTurnInFlight, TimeSpan.FromSeconds(5));

        // Second turnStart without turnEnd: stays in-flight (idempotent).
        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        await Task.Delay(100);
        Assert.True(handler.IsTurnInFlight);
    }

    // -------------------------------------------------------------------------
    // DetachedAt — set on Detach, cleared on re-Attach
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DetachedAt_SetOnDetach_ClearedOnAttach()
    {
        FakeTimeProvider time = new();
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin, time);

        Assert.Null(handler.DetachedAt); // starts with null: no connection was ever attached

        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);
        Assert.Null(handler.DetachedAt); // attached — no detached timestamp

        DateTimeOffset before = time.GetUtcNow();
        handler.Detach(conn);
        Assert.NotNull(handler.DetachedAt);
        Assert.True(handler.DetachedAt >= before);

        // Re-attach clears it.
        handler.Attach(conn, lastSeq: 0);
        Assert.Null(handler.DetachedAt);

        handler.Detach(conn);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task WaitForTaskAsync(Task task, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        try
        {
            await task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Timed out — caller will inspect task.IsCompleted.
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(20, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Condition not met within {timeout}.");
            }
        }
    }

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> backed by a static value.</summary>
    private sealed class StaticOptionsMonitor : IOptionsMonitor<GatewayOptions>
    {
        private readonly GatewayOptions _value;
        public StaticOptionsMonitor(GatewayOptions value) => _value = value;
        public GatewayOptions CurrentValue => _value;
        public GatewayOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<GatewayOptions, string?> listener) => null;
    }

    /// <summary>
    /// Records frames in delivery order.
    /// </summary>
    private sealed class RecordingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _frames.Add(frame);
                _signal.TrySetResult();
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return ValueTask.CompletedTask;
        }

        public void Abort() { }
    }

    /// <summary>
    /// Minimal in-memory <see cref="WebSocket"/> that replays a queued script of inbound
    /// messages. Stays open until a close frame is queued or the CT is cancelled.
    /// </summary>
    private sealed class FakeClientWebSocket : System.Net.WebSockets.WebSocket
    {
        private readonly Channel<(System.Net.WebSockets.WebSocketMessageType Type, byte[]? Payload)> _inbound =
            Channel.CreateUnbounded<(System.Net.WebSockets.WebSocketMessageType, byte[]?)>();

        private System.Net.WebSockets.WebSocketState _state =
            System.Net.WebSockets.WebSocketState.Open;

        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override System.Net.WebSockets.WebSocketState State => _state;

        public void QueueText(string text) =>
            _inbound.Writer.TryWrite((
                System.Net.WebSockets.WebSocketMessageType.Text,
                System.Text.Encoding.UTF8.GetBytes(text)));

        public void QueueClose() =>
            _inbound.Writer.TryWrite((System.Net.WebSockets.WebSocketMessageType.Close, null));

        public override async ValueTask<System.Net.WebSockets.ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken cancellationToken)
        {
            (System.Net.WebSockets.WebSocketMessageType type, byte[]? payload) =
                await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (type == System.Net.WebSockets.WebSocketMessageType.Close)
            {
                _state = System.Net.WebSockets.WebSocketState.CloseReceived;
                return new System.Net.WebSockets.ValueWebSocketReceiveResult(
                    0, System.Net.WebSockets.WebSocketMessageType.Close, true);
            }

            payload!.CopyTo(buffer);
            return new System.Net.WebSockets.ValueWebSocketReceiveResult(
                payload!.Length, type, true);
        }

        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Use the Memory<byte> overload.");

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            System.Net.WebSockets.WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            System.Net.WebSockets.WebSocketMessageType messageType,
            System.Net.WebSockets.WebSocketMessageFlags messageFlags,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public override Task CloseAsync(
            System.Net.WebSockets.WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = System.Net.WebSockets.WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            System.Net.WebSockets.WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = System.Net.WebSockets.WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = System.Net.WebSockets.WebSocketState.Aborted;

        public override void Dispose() { }
    }

    /// <summary>
    /// A <see cref="TextReader"/> whose <see cref="ReadLineAsync"/> blocks until a line is fed.
    /// </summary>
    private sealed class FeedableReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Feed(string line) => _lines.Writer.TryWrite(line);

        public void Complete() => _lines.Writer.TryComplete();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _lines.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public override Task<string?> ReadLineAsync() =>
            ReadLineAsync(CancellationToken.None).AsTask();
    }
}
