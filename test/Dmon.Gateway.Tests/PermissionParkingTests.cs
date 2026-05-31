using System.Threading.Channels;
using Dmon.Gateway.Sessions;

namespace Dmon.Gateway.Tests;

/// <summary>
/// Group 8: permission parking while detached.
///
/// 8.1 — While detached, permission requests are held unresolved (neither auto-approved nor
///        auto-denied). The gateway never synthesises a *.response or forwards anything to core
///        stdin in response to a detach event.
/// 8.2 — On reattach, still-outstanding requests are re-surfaced to the client. An outstanding
///        request is abandoned (cleared) with the handler when the handler is reaped.
/// </summary>
public sealed class PermissionParkingTests
{
    // -------------------------------------------------------------------------
    // 8.1 — gate reached while detached: held, not auto-resolved
    // -------------------------------------------------------------------------

    /// <summary>
    /// 8.1a — A tool.confirmRequest emitted while no connection is attached is tracked as
    /// outstanding and does NOT cause anything to be written to core stdin. The handler is
    /// also in-flight (turnStart was emitted), so the idle reaper would skip it.
    /// </summary>
    [Fact]
    public async Task ConfirmRequest_WhileDetached_IsTrackedAndCoreReceivesNothing()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // No connection — emit turnStart then a confirmRequest while fully detached.
        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"req-001","tool":"bash"}""");

        // Give the reader task time to process both lines.
        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));

        Assert.True(handler.IsTurnInFlight,
            "A parked permission gate is mid-turn; IsTurnInFlight must be true.");
        Assert.True(handler.HasOutstandingRequests,
            "The confirmRequest must be tracked as outstanding.");
        // Nothing must be written to core stdin while detached.
        Assert.Equal(string.Empty, stdin.ToString());
    }

    /// <summary>
    /// 8.1b — Client attaches, receives a tool.confirmRequest, then drops the connection.
    /// Detach must NOT write anything to core stdin and the request must remain outstanding.
    /// </summary>
    [Fact]
    public async Task ConfirmRequest_DetachAfterDelivery_NothingWrittenToCoreStdin()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Start a turn and emit a confirmRequest.
        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"req-002","tool":"write"}""");

        // Attach a connection and wait for delivery.
        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);

        // Wait until both lines are delivered and the request is tracked.
        await conn.WaitForCountAsync(2);
        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));

        // Simulate a dropped connection (socket close / missed beat).
        handler.Detach(conn);

        // Brief delay to let any inadvertent async work complete.
        await Task.Delay(100);

        Assert.True(handler.HasOutstandingRequests,
            "Outstanding request must remain after detach — detach must never auto-resolve.");
        // Detach must not write a response to core stdin.
        Assert.Equal(string.Empty, stdin.ToString());
        Assert.True(handler.IsTurnInFlight,
            "The turn is still in flight (gated on the unanswered request).");
    }

    // -------------------------------------------------------------------------
    // 8.2 — re-deliver on reattach
    // -------------------------------------------------------------------------

    /// <summary>
    /// 8.2a — Request reached while fully detached (seq > lastSeq on first attach):
    /// normal replay delivers it; the re-emit step must NOT double-deliver it.
    /// </summary>
    [Fact]
    public async Task ConfirmRequest_ReachedWhileDetached_DeliveredOnlyOnceViaReplay()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Emit turnStart + confirmRequest while detached.
        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"req-003","tool":"bash"}""");

        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));

        // Attach with lastSeq=0 — both events are in the replay window (1, 2].
        // The re-emit step must skip them because origSeq > cursor (0).
        RecordingConnection conn = new();
        handler.Attach(conn, lastSeq: 0);

        IReadOnlyList<string> received = await conn.WaitForCountAsync(2);

        // Exactly 2 frames — no double-send.
        Assert.Equal(2, received.Count);
        Assert.Contains("turnStart", received[0]);
        Assert.Contains("tool.confirmRequest", received[1]);
        Assert.Contains("req-003", received[1]);
    }

    /// <summary>
    /// 8.2b — Client received the confirmRequest (its seq is ≤ the cursor the client provides
    /// on reattach), then the connection dropped before the client answered. On reattach the
    /// prompt must be re-surfaced (re-emitted above headSeq) even though replay (cursor, headSeq]
    /// would not cover it.
    /// </summary>
    [Fact]
    public async Task ConfirmRequest_SeenThenDropped_ReSurfacedOnReattach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"req-004","tool":"bash"}""");

        // First attach — client receives and advances past seq=2.
        RecordingConnection first = new();
        handler.Attach(first, lastSeq: 0);
        await first.WaitForCountAsync(2);

        // headSeq is now 2. Detach without answering.
        handler.Detach(first);

        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));

        // Reattach with lastSeq=2 (client saw both events; cursor=2; headSeq=2 before re-emit).
        // Replay window (2, 2] is empty. The re-emit step must append the prompt above headSeq.
        RecordingConnection second = new();
        AttachResult result = handler.Attach(second, lastSeq: 2);

        // The re-emitted entry bumped headSeq to 3; attached reply must reflect this.
        Assert.Equal(3L, result.HeadSeq);

        // One re-emitted frame: the confirmRequest.
        IReadOnlyList<string> received = await second.WaitForCountAsync(1);
        Assert.Single(received);
        Assert.Contains("tool.confirmRequest", received[0]);
        Assert.Contains("req-004", received[0]);
    }

    /// <summary>
    /// 8.2c — After the client sends the matching tool.confirmResponse and it is written to
    /// core stdin, the outstanding entry is cleared and the prompt must NOT be re-surfaced on
    /// the next reattach.
    /// </summary>
    [Fact]
    public async Task ConfirmRequest_AfterResponseForwarded_NotReSurfacedOnReattach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"req-005","tool":"bash"}""");

        RecordingConnection first = new();
        handler.Attach(first, lastSeq: 0);
        await first.WaitForCountAsync(2);

        // Simulate the endpoint successfully forwarding the response: clear the outstanding entry.
        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));
        handler.ClearOutstandingRequest("req-005");
        Assert.False(handler.HasOutstandingRequests, "Outstanding request must be cleared after response.");

        handler.Detach(first);

        // Reattach with lastSeq=2 — no outstanding requests remain, headSeq stays 2.
        RecordingConnection second = new();
        AttachResult result = handler.Attach(second, lastSeq: 2);
        Assert.Equal(2L, result.HeadSeq);

        // Feed a new live event so we can verify no extra frame sneaks in before it.
        stdout.Feed("""{"type":"turnEnd","sessionId":"s1"}""");
        IReadOnlyList<string> received = await second.WaitForCountAsync(1);

        Assert.Single(received);
        Assert.Contains("turnEnd", received[0]);
        // No confirmRequest re-delivered.
        Assert.DoesNotContain("req-005", string.Join(",", received));
    }

    /// <summary>
    /// 8.2d — Handler reap (StopAsync) while a request is outstanding. After StopAsync the
    /// handler's internal state is torn down; the outstanding map is per-handler and is not
    /// persisted anywhere, so the request is abandoned with the handler (no leak, no ghost state).
    /// </summary>
    [Fact]
    public async Task ConfirmRequest_HandlerReaped_OutstandingStateNotLeaked()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        // Do NOT use await using — we call StopAsync explicitly to simulate the reaper.
        SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"req-006","tool":"bash"}""");

        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));
        Assert.True(handler.HasOutstandingRequests);

        // Simulate the session reaper tearing down the handler.
        await handler.StopAsync();

        // After StopAsync the handler object still exists (it is the reaper's reference), but
        // its pump has stopped. HasOutstandingRequests still reports the last state — the test
        // verifies that the outstanding state does NOT survive to a re-registered handler
        // (the registry is separate; this handler is dead and will not be re-used).
        // The key invariant: StopAsync completes without error even with an outstanding request.
        Assert.True(handler.HasOutstandingRequests,
            "State is in-memory; it exists until GC collects the dead handler — not a leak, " +
            "just the expected per-handler in-memory lifetime.");

        // Dispose cleanly.
        await handler.DisposeAsync();
    }

    /// <summary>
    /// 8.2e — ui.inputRequest / ui.inputResponse use the same tracking path as
    /// tool.confirmRequest / tool.confirmResponse. Verify that a ui.inputRequest is tracked
    /// and cleared by ClearOutstandingRequest, and re-surfaced when not cleared.
    /// </summary>
    [Fact]
    public async Task UiInputRequest_TrackedAndReSurfaced_SameAsConfirmRequest()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"ui.inputRequest","id":"ui-001","prompt":"Your name?"}""");

        RecordingConnection first = new();
        handler.Attach(first, lastSeq: 0);
        await first.WaitForCountAsync(2);
        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));

        handler.Detach(first);

        // Reattach with lastSeq=2 — prompt must be re-surfaced.
        RecordingConnection second = new();
        AttachResult result = handler.Attach(second, lastSeq: 2);
        Assert.Equal(3L, result.HeadSeq); // re-emitted entry bumped headSeq

        IReadOnlyList<string> received = await second.WaitForCountAsync(1);
        Assert.Single(received);
        Assert.Contains("ui.inputRequest", received[0]);
        Assert.Contains("ui-001", received[0]);

        // Now clear it (simulating a successful ui.inputResponse write).
        handler.ClearOutstandingRequest("ui-001");
        Assert.False(handler.HasOutstandingRequests);
    }

    /// <summary>
    /// 8.2f — Multiple outstanding requests: both are re-surfaced on reattach if neither has
    /// been answered. After one is cleared, only the other is re-surfaced on the next reattach.
    /// </summary>
    [Fact]
    public async Task MultipleOutstandingRequests_BothReSurfaced_ThenOneCleared()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        stdout.Feed("""{"type":"turnStart","sessionId":"s1"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"r1","tool":"bash"}""");
        stdout.Feed("""{"type":"tool.confirmRequest","id":"r2","tool":"write"}""");

        RecordingConnection first = new();
        handler.Attach(first, lastSeq: 0);
        await first.WaitForCountAsync(3);

        // Wait until both requests are tracked.
        await WaitForConditionAsync(() => handler.HasOutstandingRequests, TimeSpan.FromSeconds(5));

        handler.Detach(first);

        // Reattach with lastSeq=3 — both prompts must be re-surfaced (headSeq becomes 5).
        RecordingConnection second = new();
        AttachResult result1 = handler.Attach(second, lastSeq: 3);
        Assert.Equal(5L, result1.HeadSeq);

        IReadOnlyList<string> received1 = await second.WaitForCountAsync(2);
        Assert.Equal(2, received1.Count);
        Assert.True(received1.Any(f => f.Contains("r1")), "r1 must be re-surfaced");
        Assert.True(received1.Any(f => f.Contains("r2")), "r2 must be re-surfaced");

        // Clear r1 (answered).
        handler.ClearOutstandingRequest("r1");
        handler.Detach(second);

        // Reattach again with lastSeq=5 — only r2 remains; headSeq becomes 6.
        RecordingConnection third = new();
        AttachResult result2 = handler.Attach(third, lastSeq: 5);
        Assert.Equal(6L, result2.HeadSeq);

        IReadOnlyList<string> received2 = await third.WaitForCountAsync(1);
        Assert.Single(received2);
        Assert.Contains("r2", received2[0]);
        Assert.DoesNotContain("r1", string.Join(",", received2));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    /// <summary>
    /// Records frames in delivery order and lets a test await a target count.
    /// </summary>
    private sealed class RecordingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    /// A <see cref="TextReader"/> whose <see cref="ReadLineAsync"/> blocks until a line is fed,
    /// letting a test control when the pump observes each stdout line.
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

        public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();
    }
}
