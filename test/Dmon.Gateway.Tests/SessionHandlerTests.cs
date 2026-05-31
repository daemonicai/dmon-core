using System.Threading.Channels;
using Dmon.Gateway.Sessions;

namespace Dmon.Gateway.Tests;

public sealed class SessionHandlerTests
{
    // -------------------------------------------------------------------------
    // Pre-existing Group 2/3 regression tests (updated for seq-aware Attach)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DetachedEvents_AreBuffered_ThenDeliveredInOrderOnAttach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // No connection attached: these accumulate in the retained log.
        stdout.Feed("""{"event":"a"}""");
        stdout.Feed("""{"event":"b"}""");
        stdout.Feed("""{"event":"c"}""");

        RecordingConnection connection = new();
        // lastSeq=0: replay everything from the beginning.
        handler.Attach(connection, lastSeq: 0);

        IReadOnlyList<string> received = await connection.WaitForCountAsync(3);

        Assert.Equal(
            ["""{"event":"a"}""", """{"event":"b"}""", """{"event":"c"}"""],
            received);
    }

    [Fact]
    public async Task LiveEventAfterAttach_IsDeliveredAfterBufferedEvents()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Buffered while detached.
        stdout.Feed("""{"event":"buffered-1"}""");
        stdout.Feed("""{"event":"buffered-2"}""");

        RecordingConnection connection = new();
        handler.Attach(connection, lastSeq: 0);

        // A live event arrives right after attach; it must land after the buffered ones.
        stdout.Feed("""{"event":"live"}""");

        IReadOnlyList<string> received = await connection.WaitForCountAsync(3);

        Assert.Equal(
            ["""{"event":"buffered-1"}""", """{"event":"buffered-2"}""", """{"event":"live"}"""],
            received);
    }

    [Fact]
    public async Task Handler_SurvivesDetach_AndContinuesDeliveringAfterReattach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        RecordingConnection first = new();
        // First attach starts from the beginning.
        handler.Attach(first, lastSeq: 0);
        stdout.Feed("""{"event":"1"}""");
        await first.WaitForCountAsync(1);

        handler.Detach(first);

        // Emitted while detached → retained in log at seq 2.
        stdout.Feed("""{"event":"2"}""");

        RecordingConnection second = new();
        // lastSeq=1: the first connection saw event 1, so replay only from seq 2 onward.
        handler.Attach(second, lastSeq: 1);
        stdout.Feed("""{"event":"3"}""");

        IReadOnlyList<string> received = await second.WaitForCountAsync(2);

        Assert.Equal(["""{"event":"2"}""", """{"event":"3"}"""], received);

        // The first connection only ever saw its single live line.
        Assert.Equal(["""{"event":"1"}"""], first.Frames);
    }

    // -------------------------------------------------------------------------
    // Group 4: seq, replay, no-duplicate-across-seam, handshake headSeq
    // -------------------------------------------------------------------------

    /// <summary>
    /// 4.1 — Every event gets a strictly increasing seq; HeadSeq tracks the highest.
    /// </summary>
    [Fact]
    public async Task MonotonicSeq_AssignedToEveryEvent_NoGaps()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        int n = 5;
        for (int i = 1; i <= n; i++)
            stdout.Feed($$$"""{"event":"{{{i}}}"}""");

        RecordingConnection connection = new();
        handler.Attach(connection, lastSeq: 0);
        await connection.WaitForCountAsync(n);

        // HeadSeq must equal n — one seq per event, no gaps.
        Assert.Equal(n, handler.HeadSeq);

        // Verify events arrived in order (seq 1..n delivered in seq order).
        IReadOnlyList<string> frames = connection.Frames;
        Assert.Equal(n, frames.Count);
        for (int i = 1; i <= n; i++)
            Assert.Contains($"{i}", frames[i - 1]);
    }

    /// <summary>
    /// 4.2 — Reattach with lastSeq=N replays exactly (N, HeadSeq] in order and nothing ≤ N.
    /// </summary>
    [Fact]
    public async Task Replay_OnReattach_DeliversMissedEventsInOrder()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Feed M events while fully detached (so no connection delivers them).
        int m = 6;
        for (int i = 1; i <= m; i++)
            stdout.Feed($$$"""{"seq-test":"{{{i}}}"}""");

        // Give the reader task time to ingest all lines before attaching.
        RecordingConnection sink = new();
        handler.Attach(sink, lastSeq: 0);
        await sink.WaitForCountAsync(m);
        handler.Detach(sink);

        // Re-attach with lastSeq=3: expect only events 4, 5, 6.
        int lastSeq = 3;
        RecordingConnection replayer = new();
        handler.Attach(replayer, lastSeq: lastSeq);
        IReadOnlyList<string> received = await replayer.WaitForCountAsync(m - lastSeq);

        Assert.Equal(m - lastSeq, received.Count);
        for (int i = lastSeq + 1; i <= m; i++)
            Assert.Contains($"{i}", received[i - lastSeq - 1]);

        // Nothing ≤ lastSeq was delivered.
        foreach (string frame in received)
            for (int i = 1; i <= lastSeq; i++)
                Assert.DoesNotContain($"\"seq-test\":\"{i}\"", frame);
    }

    /// <summary>
    /// 4.3 — subscribe-then-replay: the single drain loop over (cursor, head] delivers every
    /// event exactly once regardless of whether events arrive during the replay range or after it.
    ///
    /// Structural proof: Attach sets _sentSeq = lastSeq. The loop sends events with
    /// seq &gt; _sentSeq, advancing the cursor monotonically after each success. Live events
    /// extend headSeq and wake the loop; on the next iteration the loop picks up
    /// [old_head+1..new_head] — the same predicate, no separate path. Because the cursor is the
    /// only gate and it advances monotonically, no seq can be delivered twice.
    ///
    /// This test proves the guarantee deterministically: the retained range overlaps with live
    /// events fed after attach, and we assert each event arrives exactly once, in order.
    /// </summary>
    [Fact]
    public async Task NoDuplicate_AcrossReplaySeam_LiveEventArrivingAfterAttach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Phase 1: feed events 1..4 while detached; drain them so headSeq=4.
        for (int i = 1; i <= 4; i++)
            stdout.Feed($$$"""{"ev":"{{{i}}}"}""");

        RecordingConnection drainer = new();
        handler.Attach(drainer, lastSeq: 0);
        await drainer.WaitForCountAsync(4);
        handler.Detach(drainer);

        // Phase 2: reattach with lastSeq=2 — cursor set to 2, so replay is 3..4.
        RecordingConnection connection = new();
        handler.Attach(connection, lastSeq: 2);

        // Feed two live events (seq 5, 6) after the attach. The drain loop will
        // deliver the retained 3..4 first, then pick up 5..6 on subsequent wake cycles.
        stdout.Feed("""{"ev":"5"}""");
        stdout.Feed("""{"ev":"6"}""");

        // Expect exactly 4 events: 3, 4, 5, 6 — each exactly once, in order.
        IReadOnlyList<string> received = await connection.WaitForCountAsync(4);

        Assert.Equal(4, received.Count);
        Assert.Contains("\"3\"", received[0]);
        Assert.Contains("\"4\"", received[1]);
        Assert.Contains("\"5\"", received[2]);
        Assert.Contains("\"6\"", received[3]);

        // Verify no duplicates: each expected value appears exactly once.
        string all = string.Join(",", received);
        Assert.Equal(1, received.Count(f => f.Contains("\"3\"")));
        Assert.Equal(1, received.Count(f => f.Contains("\"4\"")));
        Assert.Equal(1, received.Count(f => f.Contains("\"5\"")));
        Assert.Equal(1, received.Count(f => f.Contains("\"6\"")));
        // Events ≤ lastSeq must not appear.
        Assert.DoesNotContain("\"1\"", all);
        Assert.DoesNotContain("\"2\"", all);
    }

    /// <summary>
    /// Attach returns the correct headSeq at the moment of attach, so the attached reply
    /// carries an accurate value.
    /// </summary>
    [Fact]
    public async Task Attach_ReturnsAccurateHeadSeq_ForAttachedReply()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Feed 4 events while detached.
        for (int i = 1; i <= 4; i++)
            stdout.Feed($$$"""{"n":"{{{i}}}"}""");

        // Drain them via a throw-away connection so headSeq advances to 4.
        RecordingConnection drain = new();
        handler.Attach(drain, lastSeq: 0);
        await drain.WaitForCountAsync(4);
        handler.Detach(drain);

        Assert.Equal(4L, handler.HeadSeq);

        // New attach with lastSeq=2: result must carry generation=2, headSeq=4.
        RecordingConnection connection = new();
        AttachResult result = handler.Attach(connection, lastSeq: 2);

        Assert.Equal(2L, result.Generation);
        Assert.Equal(4L, result.HeadSeq);

        // The attached reply would carry headSeq=4; also confirm replay is 3..4 only.
        IReadOnlyList<string> received = await connection.WaitForCountAsync(2);
        Assert.Equal(2, received.Count);
        Assert.Contains("\"3\"", received[0]);
        Assert.Contains("\"4\"", received[1]);
    }

    /// <summary>
    /// 4.4 — A live event arriving while the drain loop is parked mid-replay is deduped and
    /// kept in order across the seam. The connection blocks on a gate after delivering the first
    /// replayed event; a new live event is fed while the drain is parked there; the gate is then
    /// released. Every event must be delivered exactly once and in strictly increasing seq order.
    /// (This deterministic interleave — not a post-attach feed — is what exercises B1.)
    /// </summary>
    [Fact]
    public async Task LiveEvent_ArrivingDuringReplayDrain_DeliveredExactlyOnceInOrder()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Feed events 1..3 while detached; drain them so headSeq=3.
        for (int i = 1; i <= 3; i++)
            stdout.Feed($$$"""{"ev":"{{{i}}}"}""");

        RecordingConnection warmup = new();
        handler.Attach(warmup, lastSeq: 0);
        await warmup.WaitForCountAsync(3);
        handler.Detach(warmup);

        // Gate the connection so it parks inside SendAsync after delivering the first frame.
        GatedConnection gated = new(blockAfterFrame: 1);

        // Reattach with lastSeq=0 — replay range is 1..3.
        handler.Attach(gated, lastSeq: 0);

        // Wait until the drain loop is parked mid-replay (first frame delivered, gate engaged).
        await gated.WaitUntilBlockedAsync();

        // While parked mid-replay, a live event (seq 4) arrives and extends the log.
        stdout.Feed("""{"ev":"4"}""");

        // Release the gate; the drain loop resumes and continues through the seam.
        gated.Release();

        // Expect all four events, each exactly once, in strict seq order across the seam.
        IReadOnlyList<string> received = await gated.WaitForCountAsync(4);

        Assert.Equal(4, received.Count);
        Assert.Contains("\"1\"", received[0]);
        Assert.Contains("\"2\"", received[1]);
        Assert.Contains("\"3\"", received[2]);
        Assert.Contains("\"4\"", received[3]);
        for (int i = 1; i <= 4; i++)
            Assert.Equal(1, received.Count(f => f.Contains($"\"{i}\"")));
    }

    /// <summary>
    /// 4.4b — Re-attach mid-drain must not rewind the new connection's cursor (B1 regression).
    /// Connection A parks mid-replay after delivering one frame; connection B re-attaches with a
    /// later lastSeq while A is parked; A's in-flight send is then released. B's cursor must not be
    /// rewound — B must only ever see events after its own lastSeq, never the earlier ones A was
    /// replaying.
    /// </summary>
    [Fact]
    public async Task ReattachDuringDrain_DoesNotRewindNewConnectionCursor()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Feed events 1..5 while detached; drain via a warmup connection so headSeq=5.
        for (int i = 1; i <= 5; i++)
            stdout.Feed($$$"""{"ev":"{{{i}}}"}""");

        RecordingConnection warmup = new();
        handler.Attach(warmup, lastSeq: 0);
        await warmup.WaitForCountAsync(5);
        handler.Detach(warmup);

        // Connection A attaches from the start and parks after delivering frame 1 (seq 1).
        GatedConnection connA = new(blockAfterFrame: 1);
        handler.Attach(connA, lastSeq: 0);
        await connA.WaitUntilBlockedAsync();

        // While A is parked mid-replay, connection B re-attaches with lastSeq=5. Under B1 the
        // blind lock-free advance would later write _sentSeq back to A's nextSeq (2), rewinding B.
        RecordingConnection connB = new();
        handler.Attach(connB, lastSeq: 5);

        // Release A's parked send. The drain loop must observe that _connection is no longer A
        // and refuse to advance the cursor / keep draining to A.
        connA.Release();

        // A new live event (seq 6) arrives for B. B's cursor was set to 5, so B sees only seq 6 —
        // never seq 2..5 (which would prove a rewind).
        stdout.Feed("""{"ev":"6"}""");

        IReadOnlyList<string> breceived = await connB.WaitForCountAsync(1);
        Assert.Single(breceived);
        Assert.Contains("\"6\"", breceived[0]);

        // B must never have been delivered any seq ≤ 5.
        foreach (string frame in connB.Frames)
            for (int i = 1; i <= 5; i++)
                Assert.DoesNotContain($"\"{i}\"", frame);

        // A only ever saw its single pre-park frame (seq 1); the loop stopped draining to it.
        Assert.Equal(["""{"ev":"1"}"""], connA.Frames);
    }

    /// <summary>
    /// 4.5 — Send failure leaves the cursor consistent: when SendAsync throws on a chosen seq, the
    /// cursor does not advance past the failed event, the connection is detached, and a subsequent
    /// Attach replays from the un-acked point (at-least-once; no silent skip).
    /// </summary>
    [Fact]
    public async Task SendFailure_DoesNotAdvanceCursor_AndReplaysOnReattach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        // Feed events 1..4 while detached so headSeq=4.
        for (int i = 1; i <= 4; i++)
            stdout.Feed($$$"""{"ev":"{{{i}}}"}""");

        // This connection delivers seq 1 and 2, then throws on seq 3.
        FailingConnection failing = new(throwOnFrameIndex: 3);
        handler.Attach(failing, lastSeq: 0);

        // It should deliver exactly 2 frames (1 and 2), then the throw clears the connection.
        IReadOnlyList<string> delivered = await failing.WaitForCountAsync(2);
        Assert.Equal(2, delivered.Count);
        Assert.Contains("\"1\"", delivered[0]);
        Assert.Contains("\"2\"", delivered[1]);
        await failing.WaitForFailureAsync();

        // Reattach with lastSeq=2 (the last successfully delivered seq). Replay must resume from
        // seq 3 — the un-acked event is not silently skipped.
        RecordingConnection replay = new();
        handler.Attach(replay, lastSeq: 2);
        IReadOnlyList<string> received = await replay.WaitForCountAsync(2);

        Assert.Equal(2, received.Count);
        Assert.Contains("\"3\"", received[0]);
        Assert.Contains("\"4\"", received[1]);
    }

    // -------------------------------------------------------------------------
    // Group 6: fencing and single active writer
    // -------------------------------------------------------------------------

    /// <summary>
    /// 6.1 — Successive Attach calls return strictly increasing generations.
    /// </summary>
    [Fact]
    public async Task SuccessiveAttach_ReturnsStrictlyIncreasingGenerations()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        RecordingConnection connA = new();
        RecordingConnection connB = new();
        RecordingConnection connC = new();

        AttachResult r1 = handler.Attach(connA, lastSeq: 0);
        AttachResult r2 = handler.Attach(connB, lastSeq: 0);
        AttachResult r3 = handler.Attach(connC, lastSeq: 0);

        Assert.True(r1.Generation < r2.Generation, "generation must be strictly increasing");
        Assert.True(r2.Generation < r3.Generation, "generation must be strictly increasing");
    }

    /// <summary>
    /// 6.3 — Attaching a new connection evicts and aborts the prior connection.
    /// After the second attach, connA is aborted and the handler's active connection is connB.
    /// Events drain to connB only.
    /// </summary>
    [Fact]
    public async Task NewAttach_EvictsPriorConnection_AndAbortIt()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        AbortingConnection connA = new();
        RecordingConnection connB = new();

        handler.Attach(connA, lastSeq: 0);

        // Attaching connB must abort connA.
        handler.Attach(connB, lastSeq: 0);

        Assert.True(connA.WasAborted, "prior connection must be aborted on new attach");

        // An event now drains to connB only.
        stdout.Feed("""{"ev":"x"}""");
        IReadOnlyList<string> received = await connB.WaitForCountAsync(1);
        Assert.Single(received);
        Assert.Contains("\"x\"", received[0]);
        Assert.Empty(connA.Frames);
    }

    /// <summary>
    /// 6.3 evicted Detach is a no-op — after connA is evicted and its forwarding loop calls
    /// Detach(connA), the handler's _connection is still connB.
    /// </summary>
    [Fact]
    public async Task EvictedDetach_DoesNotClobberNewConnection()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        RecordingConnection connA = new();
        RecordingConnection connB = new();

        handler.Attach(connA, lastSeq: 0);
        handler.Attach(connB, lastSeq: 0);

        // Simulate the evicted loop's cleanup: Detach(connA) must be a no-op.
        handler.Detach(connA);

        // An event must still reach connB.
        stdout.Feed("""{"ev":"y"}""");
        IReadOnlyList<string> received = await connB.WaitForCountAsync(1);
        Assert.Single(received);
        Assert.Contains("\"y\"", received[0]);
    }

    /// <summary>
    /// 6.2 — CurrentGeneration reflects the most recently attached generation.
    /// </summary>
    [Fact]
    public async Task CurrentGeneration_ReflectsMostRecentAttach()
    {
        FeedableReader stdout = new();
        StringWriter stdin = new();
        await using SessionHandler handler = new("s1", stdout, stdin);

        RecordingConnection connA = new();
        AttachResult r1 = handler.Attach(connA, lastSeq: 0);
        Assert.Equal(r1.Generation, handler.CurrentGeneration);

        RecordingConnection connB = new();
        AttachResult r2 = handler.Attach(connB, lastSeq: 0);
        Assert.Equal(r2.Generation, handler.CurrentGeneration);
        Assert.True(r2.Generation > r1.Generation);
    }

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

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
                // Replace so future waits get a fresh TCS.
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
    /// Records frames in order; after delivering frame number <c>blockAfterFrame</c> it parks
    /// inside <see cref="SendAsync"/> on a gate until <see cref="Release"/> is called. Lets a test
    /// hold the drain loop mid-replay and deterministically interleave a live event.
    /// </summary>
    private sealed class GatedConnection : IGatewayConnection
    {
        private readonly int _blockAfterFrame;
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedConnection(int blockAfterFrame) => _blockAfterFrame = blockAfterFrame;

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public async ValueTask SendAsync(string frame, CancellationToken cancellationToken)
        {
            int countAfterAdd;
            lock (_gate)
            {
                _frames.Add(frame);
                countAfterAdd = _frames.Count;
                _signal.TrySetResult();
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (countAfterAdd == _blockAfterFrame)
            {
                _blocked.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task WaitUntilBlockedAsync() => _blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => _release.TrySetResult();

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
    /// Delivers frames until the <c>throwOnFrameIndex</c>'th send (1-based), where it throws,
    /// exercising the send-failure path (cursor not advanced, connection cleared).
    /// </summary>
    private sealed class FailingConnection : IGatewayConnection
    {
        private readonly int _throwOnFrameIndex;
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
        private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _failed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FailingConnection(int throwOnFrameIndex) => _throwOnFrameIndex = throwOnFrameIndex;

        public IReadOnlyList<string> Frames
        {
            get { lock (_gate) { return [.. _frames]; } }
        }

        public ValueTask SendAsync(string frame, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_frames.Count + 1 == _throwOnFrameIndex)
                {
                    _failed.TrySetResult();
                    throw new IOException("simulated send failure");
                }

                _frames.Add(frame);
                _signal.TrySetResult();
                _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            return ValueTask.CompletedTask;
        }

        public void Abort() { }

        public Task WaitForFailureAsync() => _failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
    /// Records frames and tracks whether <see cref="Abort"/> was called.
    /// Used to verify proactive eviction in Group 6 tests.
    /// </summary>
    private sealed class AbortingConnection : IGatewayConnection
    {
        private readonly List<string> _frames = [];
        private readonly Lock _gate = new();
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
            lock (_gate) { _frames.Add(frame); }
            return ValueTask.CompletedTask;
        }

        public void Abort()
        {
            lock (_gate) { _aborted = true; }
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
