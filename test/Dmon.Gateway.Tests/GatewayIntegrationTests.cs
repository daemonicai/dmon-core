using System.Collections.Immutable;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Dmon.Gateway;
using Dmon.Gateway.DeviceKeys;
using Dmon.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 11: end-to-end integration tests that drive the gateway's public entry points —
/// <see cref="GatewayConnectionEndpoint.RunForwardingLoopForTestAsync"/> and
/// <see cref="GatewayConnectionEndpoint.HandleAsync"/> — through the real
/// <see cref="SessionHandler"/> + <see cref="SessionRegistry"/> stack.
///
/// These complement the per-group unit tests by asserting cross-component behaviour across a
/// full attach→use→drop→reattach cycle and the eviction/auth surface.
/// </summary>
public sealed class GatewayIntegrationTests
{
    // =========================================================================
    // 11.1 — Drop-and-reattach replays missed events in order, no duplicates
    // =========================================================================

    /// <summary>
    /// 11.1 — Attach → receive some events → simulate drop → reattach with <c>lastSeq=N</c>
    /// → receive only the missed events (N+1 … headSeq) in order, with no gap and no
    /// duplicate across the seam. An event emitted *during* the gap (while detached) is also
    /// delivered exactly once on reattach, exercising the subscribe-then-replay seam (§4).
    /// </summary>
    [Fact]
    public async Task DropAndReattach_ReplaysMissedEvents_InOrder_NoDuplicate()
    {
        // Arrange: a handler backed by a controlled stdout reader.
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s-replay", stdout, stdin);

        // Feed four events so they accumulate in the seq log before any attach.
        stdout.Feed("""{"event":"e1"}""");
        stdout.Feed("""{"event":"e2"}""");
        stdout.Feed("""{"event":"e3"}""");

        // --- First attach: consume events e1 and e2 only (lastSeq=0 → replay all 3) ---
        RecordingConnection conn1 = new();
        handler.Attach(conn1, lastSeq: 0);

        // Wait for all three to be delivered (seq 1, 2, 3).
        IReadOnlyList<string> firstBatch = await conn1.WaitForCountAsync(3);
        Assert.Equal(3, firstBatch.Count);

        // Simulate drop: detach after consuming events up to seq 2 (e1 and e2).
        // We tell the handler about this by detaching; the client will report lastSeq=2 on reattach.
        handler.Detach(conn1);

        // An event arrives WHILE detached — must be buffered and replayed on reattach.
        stdout.Feed("""{"event":"e4"}""");

        // Brief pause to let the pump process e4 into the seq log before reattach.
        await WaitForConditionAsync(() => handler.HeadSeq >= 4, TimeSpan.FromSeconds(5));

        // --- Second attach: client reports lastSeq=2; expects e3, e4 (not e1, e2). ---
        RecordingConnection conn2 = new();
        handler.Attach(conn2, lastSeq: 2);

        IReadOnlyList<string> secondBatch = await conn2.WaitForCountAsync(2);

        // Exactly two events replayed: e3 and e4, in order, with no duplicates.
        Assert.Equal(2, secondBatch.Count);
        Assert.Contains("""{"event":"e3"}""", secondBatch[0]);
        Assert.Contains("""{"event":"e4"}""", secondBatch[1]);

        // Verify no e1/e2 leaked across the seam.
        Assert.DoesNotContain(secondBatch, f => f.Contains("\"e1\"") || f.Contains("\"e2\""));

        handler.Detach(conn2);
    }

    /// <summary>
    /// 11.1b — Drop and reattach with lastSeq=0 replays ALL events in order (full-replay path).
    /// Confirms that no event is skipped and the sequence is gapless (seq 1, 2, 3, 4, 5).
    /// </summary>
    [Fact]
    public async Task DropAndReattach_WithLastSeqZero_ReplaysAllEventsInOrder()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s-full-replay", stdout, stdin);

        int n = 5;
        for (int i = 1; i <= n; i++)
            stdout.Feed($$$"""{"event":"{{{i}}}"}""");

        // First attach: drain all events.
        RecordingConnection conn1 = new();
        handler.Attach(conn1, lastSeq: 0);
        await conn1.WaitForCountAsync(n);
        handler.Detach(conn1);

        // Wait for all events to be logged.
        await WaitForConditionAsync(() => handler.HeadSeq >= n, TimeSpan.FromSeconds(5));

        // Reattach with lastSeq=0 — must get all n events again, in order.
        RecordingConnection conn2 = new();
        handler.Attach(conn2, lastSeq: 0);
        IReadOnlyList<string> replayed = await conn2.WaitForCountAsync(n);

        Assert.Equal(n, replayed.Count);
        for (int i = 1; i <= n; i++)
            Assert.Contains($"\"{i}\"", replayed[i - 1]);

        handler.Detach(conn2);
    }

    // =========================================================================
    // 11.2a — Resent command is deduped by id; across a reattach via the forwarding loop
    // =========================================================================

    /// <summary>
    /// 11.2a — A command forwarded on connection 1 is deduped when the SAME <c>id</c> arrives
    /// on connection 2 (simulating a reattach resend). Core stdin sees it exactly once; the
    /// gateway re-acks on the second connection.
    ///
    /// This drives <see cref="GatewayConnectionEndpoint.RunForwardingLoopForTestAsync"/> across
    /// two real attach/forward cycles against the same <see cref="SessionHandler"/>.
    /// </summary>
    [Fact]
    public async Task Dedupe_AcrossReattach_CommandForwardedOnce_ReAckedOnSecondConnection()
    {
        const string cmd = """{"id":"req-dedup","type":"run","prompt":"hello"}""";

        CapturingWriter stdin = new();
        await using SessionHandler handler = new("s-dedup", new NeverReadingReader(), stdin);

        GatewayConnectionEndpoint endpoint = MakeEndpoint();

        // --- Connection 1: command admitted and forwarded. ---
        FakeClientWebSocket socket1 = new();
        socket1.QueueText(cmd);
        socket1.QueueClose();

        RecordingConnection conn1 = new();
        await endpoint.RunForwardingLoopForTestAsync(socket1, conn1, handler, CancellationToken.None);

        // Core saw the command once.
        Assert.Equal(cmd + "\n", stdin.GetWritten());

        // Ack sent on connection 1.
        Assert.Contains(conn1.Frames, f => f.Contains("\"ack\"") && f.Contains("req-dedup"));

        // --- Connection 2 (reattach): resend the SAME id. ---
        stdin.Reset();
        FakeClientWebSocket socket2 = new();
        socket2.QueueText(cmd); // same id
        socket2.QueueClose();

        RecordingConnection conn2 = new();
        await endpoint.RunForwardingLoopForTestAsync(socket2, conn2, handler, CancellationToken.None);

        // Core must NOT receive the duplicate.
        Assert.Equal(string.Empty, stdin.GetWritten());

        // But the gateway re-acks on connection 2 (client may not have seen the first ack).
        Assert.Contains(conn2.Frames, f => f.Contains("\"ack\"") && f.Contains("req-dedup"));
    }

    // =========================================================================
    // 11.2b — In-flight handler survives idle TTL; idle handler reaps at TTL
    // =========================================================================

    /// <summary>
    /// 11.2b-i — A detached handler with a turn in flight is NOT reaped at the idle TTL;
    /// it is only reaped once the absolute-max TTL elapses.
    ///
    /// Drives <see cref="SessionReaper.ScanAndReapAsync"/> with a <see cref="FakeTimeProvider"/>
    /// — no wall-clock sleeps.
    /// </summary>
    [Fact]
    public async Task Reaper_InFlightHandler_SurvivesIdleTtl_ReapedAtAbsoluteMax()
    {
        FakeTimeProvider time = new();
        SessionRegistry registry = new();
        IOptionsMonitor<GatewayOptions> options = MakeOptions(new GatewayOptions
        {
            IdleDetachedTtlMinutes = 15,
            RunningTurnTtlMinutes = 60,
        });

        // Not await using: the reaper takes ownership and disposes on removal.
        FeedableReader stdout = new();
        StringWriter stdin = new();
        SessionHandler handler = new("s-inflight", stdout, stdin, time);
        registry.Register("s-inflight", handler);

        // Feed a turnStart so IsTurnInFlight = true.
        stdout.Feed("""{"type":"turnStart","sessionId":"s-inflight"}""");
        await WaitForConditionAsync(() => handler.IsTurnInFlight, TimeSpan.FromSeconds(5));

        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);
        await conn.WaitForCountAsync(1); // wait for turnStart to be delivered
        handler.Detach(conn);

        Assert.True(handler.IsTurnInFlight);

        SessionReaper reaper = new(registry, options, time, NullLogger<SessionReaper>.Instance);

        // Advance past idle TTL — must NOT reap (turn in flight).
        time.Advance(TimeSpan.FromMinutes(16));
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.NotNull(registry.TryGet("s-inflight")); // still alive

        // Advance past the absolute-max TTL — must reap.
        time.Advance(TimeSpan.FromMinutes(50)); // total > 60 min
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.Null(registry.TryGet("s-inflight")); // finally reaped
    }

    /// <summary>
    /// 11.2b-ii — A detached idle handler (no turn in flight) is reaped once the idle TTL elapses
    /// and NOT before, driven with a <see cref="FakeTimeProvider"/> — no wall-clock sleeps.
    /// </summary>
    [Fact]
    public async Task Reaper_IdleDetachedHandler_ReapedAtIdleTtl_NotBefore()
    {
        FakeTimeProvider time = new();
        SessionRegistry registry = new();
        IOptionsMonitor<GatewayOptions> options = MakeOptions(new GatewayOptions
        {
            IdleDetachedTtlMinutes = 15,
            RunningTurnTtlMinutes = 60,
        });

        // Not await using: the reaper disposes on removal.
        FeedableReader stdout = new();
        StringWriter stdin = new();
        SessionHandler handler = new("s-idle", stdout, stdin, time);
        registry.Register("s-idle", handler);

        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);
        handler.Detach(conn);

        Assert.False(handler.IsTurnInFlight);

        SessionReaper reaper = new(registry, options, time, NullLogger<SessionReaper>.Instance);

        // Before idle TTL elapses — must NOT reap.
        time.Advance(TimeSpan.FromMinutes(14));
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.NotNull(registry.TryGet("s-idle")); // still alive

        // Past idle TTL — must reap.
        time.Advance(TimeSpan.FromMinutes(2)); // total = 16 min > 15 min TTL
        await reaper.ScanAndReapAsync(CancellationToken.None);
        Assert.Null(registry.TryGet("s-idle")); // reaped
    }

    // =========================================================================
    // 11.3a — New attach evicts the prior connection; fenced frames are rejected
    // =========================================================================

    /// <summary>
    /// 11.3a — Attaching a second connection evicts the first (prior socket aborted/closed),
    /// and any frame that arrives on the first connection's forwarding loop after eviction is
    /// rejected (fenced) and the socket is closed with status 4409.
    ///
    /// Drives two sequential <c>RunForwardingLoopForTestAsync</c> calls against one handler:
    /// connA attaches (gen G), connB attaches (gen G+1) — proactively evicting connA via
    /// <see cref="IGatewayConnection.Abort"/> — then connA's loop (still running in the eviction
    /// window) receives a frame and must detect the stale generation, drop the frame, and exit.
    /// </summary>
    [Fact]
    public async Task NewAttach_EvictsPriorConnection_FencedFrameRejected()
    {
        const string command = """{"id":"req-evict","type":"run","prompt":"should not reach core"}""";

        CapturingWriter stdin = new();
        await using SessionHandler handler = new("s-evict", new NeverReadingReader(), stdin);

        GatewayConnectionEndpoint endpoint = MakeEndpoint();

        // --- connA attaches and gets its generation. ---
        RecordingConnection connA = new();
        AttachResult resultA = handler.Attach(connA, lastSeq: 0);

        // --- connB attaches: this evicts connA (Abort is called synchronously). ---
        RecordingConnection connB = new();
        handler.Attach(connB, lastSeq: 0);

        // Assert connA was aborted by the new attach.
        Assert.True(connA.WasAborted, "prior connection must be aborted when superseded");

        // Now drive connA's forwarding loop with its stale generation.
        // Despite the command arriving, it must NOT be forwarded to core stdin.
        FakeClientWebSocket socketA = new();
        socketA.QueueText(command);
        socketA.QueueClose(); // fallback termination if fencing does not close first

        await endpoint.RunForwardingLoopForTestAsync(
            socketA, connA, handler, CancellationToken.None,
            myGeneration: resultA.Generation,
            enforceFencing: true);

        // Frame must not have reached the core.
        Assert.Equal(string.Empty, stdin.GetWritten());

        // Socket must be in a terminal state (fencing closes it with 4409).
        Assert.True(
            socketA.State is WebSocketState.Closed or WebSocketState.Aborted,
            $"Expected socket closed after fencing; state was {socketA.State}");

        // connA must not have been sent any ack/pong.
        Assert.DoesNotContain(connA.Frames, f => f.Contains("\"ack\"") || f.Contains("\"pong\""));

        handler.Detach(connB);
    }

    // =========================================================================
    // 11.3b — Device-key mismatch yields HTTP 401 before socket opened
    // =========================================================================

    /// <summary>
    /// 11.3b — When the device-key set is non-empty and the client sends a wrong (or missing)
    /// <c>Authorization</c> header, <see cref="GatewayConnectionEndpoint.HandleAsync"/>
    /// responds with HTTP 401 <em>before</em> opening a WebSocket, preserving the §9 security
    /// boundary.
    ///
    /// Reuses the <see cref="DefaultHttpContext"/> approach from <c>AuthAndBindTests</c>.
    /// </summary>
    [Theory]
    [InlineData(null)]                  // no header
    [InlineData("Bearer wrong-key")]    // wrong token
    [InlineData("Basic dXNlcjpwYXNz")] // wrong scheme
    public async Task DeviceKeyMismatch_Returns401_NoSocketOpened(string? authorizationHeader)
    {
        GatewayConnectionEndpoint endpoint = MakeEndpointWithKey("correct-key");
        DefaultHttpContext context = new();

        if (authorizationHeader is not null)
            context.Request.Headers.Authorization = authorizationHeader;

        await endpoint.HandleAsync(context);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>
    /// 11.3b (control) — With an empty key set auth is disabled,
    /// so the request falls through to the non-WebSocket check (400).
    /// </summary>
    [Fact]
    public async Task EmptyKeySet_NoHeader_AuthCheckDisabled()
    {
        GatewayConnectionEndpoint endpoint = MakeEndpointWithKey(token: null);
        DefaultHttpContext context = new();

        await endpoint.HandleAsync(context);

        Assert.NotEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // =========================================================================
    // 11.4 — Handshake timeout: DriveSessionHandshakeAsync exits on cancellation
    // =========================================================================

    /// <summary>
    /// 11.4 — A core that passes agentReady but then stays silent causes
    /// <see cref="GatewayConnectionEndpoint.DriveSessionHandshakeAsync"/> to throw
    /// <see cref="OperationCanceledException"/> when the supplied token is cancelled
    /// (simulating <c>CreateHandshakeTimeoutSeconds</c> expiry).
    ///
    /// This confirms the liveness gap fix: the correlated-result reader exits on cancellation
    /// and does NOT hang indefinitely on a live-but-silent core.
    /// </summary>
    [Fact]
    public async Task DriveSessionHandshake_TimesOut_WhenCoreIsSilent()
    {
        // stdout that blocks until cancellation and then propagates OperationCanceledException —
        // matching the real StreamReader behaviour when ReadLineAsync is cancelled.
        BlockingReader silentStdout = new();
        StringWriter stdin = new();

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GatewayConnectionEndpoint.DriveSessionHandshakeAsync(
                silentStdout, stdin, profile: null, cts.Token));
    }

    /// <summary>
    /// 11.4b — <see cref="GatewayOptions.CreateHandshakeTimeoutSeconds"/> is surfaced and
    /// defaults to 30, so the option is discoverable and never zero.
    /// </summary>
    [Fact]
    public void GatewayOptions_CreateHandshakeTimeoutSeconds_DefaultsTo30()
    {
        GatewayOptions opts = new();
        Assert.Equal(30, opts.CreateHandshakeTimeoutSeconds);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static GatewayConnectionEndpoint MakeEndpoint() =>
        new(new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions(),
            NullLogger<GatewayConnectionEndpoint>.Instance);

    /// <summary>
    /// Builds an endpoint whose device-key set contains a single entry for <paramref name="token"/>,
    /// or is empty when <paramref name="token"/> is <see langword="null"/> (auth disabled).
    /// </summary>
    private static GatewayConnectionEndpoint MakeEndpointWithKey(string? token)
    {
        DeviceKeySet keySet = token is null
            ? DeviceKeySet.Empty
            : new DeviceKeySet(ImmutableArray.Create(new DeviceCredential(
                KeyId: "test-key",
                Name: "test-key",
                SecretHash: Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(),
                CreatedAt: DateTimeOffset.UtcNow,
                RevokedAt: null)));

        return new GatewayConnectionEndpoint(
            new SessionRegistry(),
            new GatewayConnectionEndpoint.TestOptions
            {
                DeviceKeySetProvider = new DeviceKeySetProvider(keySet),
            },
            NullLogger<GatewayConnectionEndpoint>.Instance);
    }

    private static IOptionsMonitor<GatewayOptions> MakeOptions(GatewayOptions opts) =>
        new StaticOptionsMonitor(opts);

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        using CancellationTokenSource cts = new(timeout);
        while (!condition())
        {
            try
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
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
    /// Records frames in delivery order and lets a test await a target count.
    /// Also tracks whether <see cref="Abort"/> was called (needed for 11.3a).
    /// </summary>
    private sealed class RecordingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _aborted;

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public bool WasAborted
        {
            get { lock (_gate) { return _aborted; } }
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

        public void Abort()
        {
            lock (_gate) { _aborted = true; }
        }

        public async Task<IReadOnlyList<string>> WaitForCountAsync(int count)
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            while (true)
            {
                Task waitTask;
                lock (_gate)
                {
                    if (_frames.Count >= count)
                        return [.. _frames];
                    waitTask = _signal.Task;
                }

                try
                {
                    await waitTask.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        throw new TimeoutException(
                            $"Expected {count} frames, saw {_frames.Count}: [{string.Join(", ", _frames)}]");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="WebSocket"/> that replays a queued script of inbound messages.
    /// Tracks close/abort state so tests can assert fencing closed the socket.
    /// </summary>
    private sealed class FakeClientWebSocket : WebSocket
    {
        private readonly Channel<(WebSocketMessageType Type, byte[] Payload)> _inbound =
            Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();

        private WebSocketState _state = WebSocketState.Open;

        public void QueueText(string frame) =>
            _inbound.Writer.TryWrite((WebSocketMessageType.Text, Encoding.UTF8.GetBytes(frame)));

        public void QueueClose() =>
            _inbound.Writer.TryWrite((WebSocketMessageType.Close, []));

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            (WebSocketMessageType type, byte[] payload) =
                await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (type == WebSocketMessageType.Close)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            int count = Math.Min(payload.Length, buffer.Count);
            Array.Copy(payload, 0, buffer.Array!, buffer.Offset, count);
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, true);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() { }
    }

    /// <summary>
    /// A stdout reader that blocks indefinitely — the pump stays idle.
    /// Swallows <see cref="OperationCanceledException"/> and returns null (EOF),
    /// matching the pattern required by <see cref="SessionHandler"/>'s pump loop.
    /// Not suitable for cancellation-propagation tests — use <see cref="BlockingReader"/> there.
    /// </summary>
    private sealed class NeverReadingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            return null;
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    /// <summary>
    /// A stdout reader that blocks until cancellation and then lets
    /// <see cref="OperationCanceledException"/> propagate — matching the real
    /// <see cref="StreamReader.ReadLineAsync(CancellationToken)"/> behaviour.
    /// Use this to assert that a timeout-linked token actually unblocks callers.
    /// </summary>
    private sealed class BlockingReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null; // unreachable; satisfies the compiler.
        }

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }

    /// <summary>A <see cref="TextReader"/> whose <see cref="ReadLineAsync"/> blocks until a line is fed.</summary>
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

    /// <summary>Captures text written to core stdin. Thread-safe reset for multi-connection tests.</summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly object _lock = new();
        private readonly StringBuilder _sb = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock) { _sb.Append(buffer.Span); }
            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public string GetWritten() { lock (_lock) { return _sb.ToString(); } }

        public void Reset() { lock (_lock) { _sb.Clear(); } }
    }
}
